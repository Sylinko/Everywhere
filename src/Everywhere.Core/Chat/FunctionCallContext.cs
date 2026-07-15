using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat;

/// <summary>
/// Represents the ambient state of one concrete tool invocation.
/// </summary>
/// <remarks>
/// A <see cref="FunctionCallChatMessage"/> may aggregate multiple calls to the same function. This
/// context intentionally belongs to one <see cref="FunctionCallContent"/> instead of that aggregate
/// message, allowing its AsyncLocal value and activity preview to remain unambiguous when calls are
/// executed concurrently in the future.
/// </remarks>
public sealed class FunctionCallContext : IChatPluginUserInterface, IDisposable
{
    public Kernel Kernel { get; }

    public ChatContext ChatContext { get; }

    public ChatPlugin ChatPlugin { get; }

    public ChatFunction ChatFunction { get; }

    public FunctionCallChatMessage FunctionCallChatMessage { get; }

    public FunctionCallContent FunctionCallContent { get; }

    /// <summary>Gets the stable tool-call ID used to isolate transient invocation state.</summary>
    public string InvocationId { get; }

    public IDictionary<string, bool> IsPermissionGrantedRecords { get; }

    public IChatPluginDisplaySink DisplaySink { get; }

    /// <summary>
    /// Gets or sets the lightweight preview owned exclusively by this invocation.
    /// </summary>
    /// <remarks>
    /// Assigning the property replaces one stable slot; it does not mutate the aggregate
    /// invocation registry. The slot is removed as a whole when this context is disposed, so
    /// callers normally do not need to assign <see langword="null"/> explicitly.
    /// </remarks>
    public ChatPluginActivityPreview? ActivityPreview
    {
        get => _activityPreviewSlot.Preview;
        set => _activityPreviewSlot.Preview = value;
    }

    private readonly FunctionCallChatMessage.ActivityPreviewSlot _activityPreviewSlot;

    public FunctionCallContext(
        Kernel kernel,
        ChatContext chatContext,
        ChatPlugin chatPlugin,
        ChatFunction chatFunction,
        FunctionCallChatMessage functionCallChatMessage,
        FunctionCallContent functionCallContent,
        IDictionary<string, bool> isPermissionGrantedRecords)
    {
        if (functionCallContent.Id.IsNullOrEmpty())
            throw new ArgumentException("A function call context requires a non-empty tool-call ID.", nameof(functionCallContent));

        Kernel = kernel;
        ChatContext = chatContext;
        ChatPlugin = chatPlugin;
        ChatFunction = chatFunction;
        FunctionCallChatMessage = functionCallChatMessage;
        FunctionCallContent = functionCallContent;
        InvocationId = functionCallContent.Id;
        IsPermissionGrantedRecords = isPermissionGrantedRecords;
        DisplaySink = functionCallChatMessage.DisplaySink;
        _activityPreviewSlot = functionCallChatMessage.RegisterActivityPreview(InvocationId);
    }

    public string PermissionKey => $"{ChatPlugin.Key}.{ChatFunction.KernelFunction.Name}";

    public bool IsPermissionGranted
    {
        get
        {
            var permissionKey = PermissionKey;

            // If the function requires permissions that are less than FileAccess, we consider it as low-risk and grant permission by default.
            if (ChatFunction.Permissions <= ChatFunctionPermissions.AutoGranted &&
                (!ChatContext.IsPermissionGrantedRecords.ContainsKey(permissionKey) || ChatContext.IsPermissionGrantedRecords[permissionKey]))
            {
                return true;
            }

            ChatContext.IsPermissionGrantedRecords.TryGetValue(permissionKey, out var isSessionGranted);
            return isSessionGranted;
        }
    }

    #region IChatPluginUserInterface implementation

    public async Task<RequestConsentResult> RequestConsentAsync(
        string? id,
        IDynamicLocaleKey headerKey,
        ChatPluginDisplayBlock? content = null,
        RequestConsentRememberMasks rememberMasks = RequestConsentRememberMasks.All,
        CancellationToken cancellationToken = default)
    {
        if (id.IsNullOrEmpty() && ChatFunction.AutoApprove) return RequestConsentResult.Accepted;

        var permissionKey = id.IsNullOrEmpty() ? PermissionKey : $"{PermissionKey}.{id}";
        IsPermissionGrantedRecords.TryGetValue(permissionKey, out var isGloballyGranted);
        ChatContext.IsPermissionGrantedRecords.TryGetValue(permissionKey, out var isSessionGranted);
        if (isGloballyGranted || isSessionGranted)
        {
            return RequestConsentResult.Accepted;
        }

        var consentDecision = await ChatContext.UserInterfaceBroker.HandleConsentRequestAsync(
            headerKey,
            content,
            rememberMasks,
            cancellationToken);

        switch (consentDecision.Decision)
        {
            case ConsentDecision.AlwaysAllow:
            {
                IsPermissionGrantedRecords[permissionKey] = true;
                return RequestConsentResult.Accepted;
            }
            case ConsentDecision.AllowSession:
            {
                ChatContext.IsPermissionGrantedRecords[permissionKey] = true;
                return RequestConsentResult.Accepted;
            }
            case ConsentDecision.AllowOnce:
            {
                return RequestConsentResult.Accepted;
            }
            case ConsentDecision.Deny:
            default:
            {
                return RequestConsentResult.Denied(consentDecision.Reason);
            }
        }
    }

    public Task<IReadOnlyList<ChatPluginQuestionAnswer>> AskQuestionAsync(
        IReadOnlyList<ChatPluginQuestion> questions,
        CancellationToken cancellationToken = default)
    {
        return ChatContext.UserInterfaceBroker.HandleAskQuestionAsync(questions, cancellationToken);
    }

    #endregion

    /// <summary>
    /// Ends the invocation-scoped preview lifetime. Removing the stable slot cannot clear or
    /// overwrite a preview owned by another invocation in the same aggregate message.
    /// </summary>
    public void Dispose() => FunctionCallChatMessage.UnregisterActivityPreview(InvocationId, _activityPreviewSlot);
}