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

    public ToolAutoApprovalSettings ToolAutoApproval { get; }

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
        get => _activityPresentationSlot.Preview;
        set => _activityPresentationSlot.Preview = value;
    }

    private readonly FunctionCallChatMessage.ActivityPresentationSlot _activityPresentationSlot;

    public FunctionCallContext(
        Kernel kernel,
        ChatContext chatContext,
        ChatPlugin chatPlugin,
        ChatFunction chatFunction,
        FunctionCallChatMessage functionCallChatMessage,
        FunctionCallContent functionCallContent,
        ToolAutoApprovalSettings toolAutoApproval)
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
        ToolAutoApproval = toolAutoApproval;
        DisplaySink = functionCallChatMessage.DisplaySink;
        _activityPresentationSlot = functionCallChatMessage.RegisterActivityPresentation(InvocationId);
    }

    public string PermissionKey => ToolSettingsKey.ForFunction(ChatPlugin, ChatFunction);

    public bool IsPermissionGranted
    {
        get
        {
            var permissionKey = PermissionKey;

            if (IsGloballyAutoApproved() &&
                (!ChatContext.ToolAutoApproval.ContainsKey(permissionKey) || ChatContext.ToolAutoApproval[permissionKey]))
            {
                return true;
            }

            ChatContext.ToolAutoApproval.TryGetValue(permissionKey, out var isSessionGranted);
            return isSessionGranted;
        }
    }

    #region IChatPluginUserInterface implementation

    public async Task<RequestConsentResult> RequestConsentAsync(
        string? id,
        IDynamicLocaleKey headerKey,
        ChatPluginDisplayBlock? content = null,
        RequestConsentRememberMasks rememberMasks = RequestConsentRememberMasks.All,
        IReadOnlyList<RequestConsentCustomOption>? customOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (id.IsNullOrEmpty() && IsGloballyAutoApproved()) return RequestConsentResult.Accept;

        var permissionKey = ToolSettingsKey.ForPermission(ChatPlugin, ChatFunction, id);
        ToolAutoApproval.TryGetValue(permissionKey, out var isGloballyGranted);
        ChatContext.ToolAutoApproval.TryGetValue(permissionKey, out var isSessionGranted);
        if (isGloballyGranted || isSessionGranted)
        {
            return RequestConsentResult.Accept;
        }

        var consentDecision = await WaitForUserInputAsync(() => ChatContext.UserInterfaceBroker.HandleConsentRequestAsync(
            headerKey,
            content,
            rememberMasks,
            customOptions,
            cancellationToken));

        switch (consentDecision.Kind)
        {
            case ConsentDecisionKind.AlwaysAllow:
            {
                ToolAutoApproval[permissionKey] = true;
                return RequestConsentResult.Accept;
            }
            case ConsentDecisionKind.AllowSession:
            {
                ChatContext.ToolAutoApproval[permissionKey] = true;
                return RequestConsentResult.Accept;
            }
            case ConsentDecisionKind.AllowOnce:
            {
                return RequestConsentResult.Accept;
            }
            case ConsentDecisionKind.Custom when consentDecision.CustomOption is { } customOption:
            {
                return RequestConsentResult.Custom(customOption);
            }
            case ConsentDecisionKind.Deny:
            default:
            {
                return RequestConsentResult.Deny(consentDecision.Reason);
            }
        }
    }

    public async Task<IReadOnlyList<ChatPluginQuestionAnswer>> AskQuestionAsync(
        IReadOnlyList<ChatPluginQuestion> questions,
        CancellationToken cancellationToken = default)
    {
        return await WaitForUserInputAsync(() => ChatContext.UserInterfaceBroker.HandleAskQuestionAsync(questions, cancellationToken));
    }

    #endregion

    private bool IsGloballyAutoApproved()
    {
        if (!ChatFunction.IsAutoApproveAllowed) return false;
        return ToolAutoApproval.TryGetValue(PermissionKey, out var value) ? value : ChatFunction.IsDefaultAutoApprove;
    }

    /// <summary>
    /// Runs an interaction inside this invocation's transient user-input wait state.
    /// </summary>
    /// <remarks>
    /// The slot uses an atomic counter rather than a Boolean so overlapping interactions cannot
    /// clear each other's state. The <see langword="finally"/> block also guarantees that
    /// cancellation and broker exceptions restore the aggregate activity state.
    /// </remarks>
    public async Task<T> WaitForUserInputAsync<T>(Func<Task<T>> interaction)
    {
        _activityPresentationSlot.EnterUserInputWait();
        try
        {
            return await interaction();
        }
        finally
        {
            _activityPresentationSlot.ExitUserInputWait();
        }
    }

    /// <summary>
    /// Ends the invocation-scoped presentation lifetime. Removing the stable slot cannot clear or
    /// overwrite state owned by another invocation in the same aggregate message.
    /// </summary>
    public void Dispose() =>
        FunctionCallChatMessage.UnregisterActivityPresentation(InvocationId, _activityPresentationSlot);
}
