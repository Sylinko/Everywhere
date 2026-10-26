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
public sealed class FunctionCallContext : IChatPluginUserInterface, IChatPluginDisplaySink, IAsyncDisposable
{
    public Kernel Kernel { get; }

    public ChatContext ChatContext { get; }

    public ChatPlugin ChatPlugin { get; }

    public ChatFunction ChatFunction { get; }

    public FunctionCallChatMessage FunctionCallChatMessage { get; }

    public FunctionCallContent FunctionCallContent { get; }

    public GenerationContext GenerationContext { get; }

    /// <summary>
    /// Gets the approval mode captured before this tool started.
    /// </summary>
    public ToolApprovalMode ApprovalMode { get; }

    /// <summary>
    /// Gets whether a required consent was denied during this invocation.</summary>
    public bool HasApprovalDenied { get; private set; }

    /// <summary>
    /// Gets the latest automatic-review failure for invocation outcome reporting.
    /// </summary>
    public ToolApprovalFailure? ApprovalFailure { get; private set; }

    /// <summary>
    /// Gets the stable tool-call ID used to isolate transient invocation state.
    /// </summary>
    public string InvocationId { get; }

    public ObservableToolRulesets ToolBypassApprovalRulesets { get; }

    public IChatPluginDisplaySink DisplaySink => this;

    /// <summary>
    /// Gets whether the current function call may bypass approval.
    /// </summary>
    /// <remarks>
    /// Path-scoped approvals are evaluated separately by file-system operations and do not alter this value.
    /// </remarks>
    public bool BypassesApproval => ApprovalMode == ToolApprovalMode.FullAccess ||
        ToolBypassApprovalPolicy.BypassesApproval(ToolBypassApprovalRulesets, ChatPlugin, ChatFunction);

    private readonly FunctionCallChatMessage.ActivityPresentationSlot _activityPresentationSlot;
    private readonly Func<FunctionCallContext, ToolApprovalScope?, CancellationToken, Task<ToolApprovalResult>> _reviewAsync;

    /// <summary>
    /// Creates the invocation and registers its presentation slot inside the root execution domain.
    /// The caller awaits disposal before completing the enclosing tool operation.
    /// </summary>
    public FunctionCallContext(
        Kernel kernel,
        ChatContext chatContext,
        ChatPlugin chatPlugin,
        ChatFunction chatFunction,
        FunctionCallChatMessage functionCallChatMessage,
        FunctionCallContent functionCallContent,
        ObservableToolRulesets toolBypassApprovalRulesets,
        GenerationContext generationContext,
        Func<FunctionCallContext, ToolApprovalScope?, CancellationToken, Task<ToolApprovalResult>> reviewAsync)
    {
        if (functionCallContent.Id.IsNullOrEmpty())
        {
            throw new ArgumentException("A function call context requires a non-empty tool-call ID.", nameof(functionCallContent));
        }

        Kernel = kernel;
        ChatContext = chatContext;
        ChatPlugin = chatPlugin;
        ChatFunction = chatFunction;
        FunctionCallChatMessage = functionCallChatMessage;
        FunctionCallContent = functionCallContent;
        GenerationContext = generationContext;
        ApprovalMode = generationContext.ApprovalMode;
        _reviewAsync = reviewAsync;
        InvocationId = functionCallContent.Id;
        ToolBypassApprovalRulesets = toolBypassApprovalRulesets;
        _activityPresentationSlot = functionCallChatMessage.RegisterActivityPresentation(InvocationId);
    }

    public string PermissionKey => ToolSettingsKey.ForFunction(ChatPlugin, ChatFunction);

    public bool IsPermissionGranted
    {
        get
        {
            var permissionKey = PermissionKey;

            if (BypassesApproval &&
                (!ChatContext.ToolBypassApprovalRulesets.ContainsKey(permissionKey) || ChatContext.ToolBypassApprovalRulesets[permissionKey]))
            {
                return true;
            }

            ChatContext.ToolBypassApprovalRulesets.TryGetValue(permissionKey, out var isSessionGranted);
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
        ToolApprovalScope? approvalScope = null,
        CancellationToken cancellationToken = default)
    {
        if (ApprovalMode == ToolApprovalMode.FullAccess || id.IsNullOrEmpty() && BypassesApproval)
        {
            return RequestConsentResult.Accept;
        }

        var permissionKey = ToolSettingsKey.ForPermission(ChatPlugin, ChatFunction, id);
        ToolBypassApprovalRulesets.TryGetValue(permissionKey, out var isGloballyGranted);
        ChatContext.ToolBypassApprovalRulesets.TryGetValue(permissionKey, out var isSessionGranted);
        if (isGloballyGranted || isSessionGranted)
        {
            return RequestConsentResult.Accept;
        }

        if (ApprovalMode == ToolApprovalMode.Auto)
        {
            var review = await ReviewApprovalAsync(approvalScope, cancellationToken);
            return review.IsAllowed ? RequestConsentResult.Accept : RequestConsentResult.Deny(review.FormatReason());
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
                ToolBypassApprovalRulesets[permissionKey] = true;
                return RequestConsentResult.Accept;
            }
            case ConsentDecisionKind.AllowSession:
            {
                ChatContext.ToolBypassApprovalRulesets[permissionKey] = true;
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
                HasApprovalDenied = true;
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

    /// <summary>Reviews this invocation's required consent without entering human-input wait state.</summary>
    public async Task<ToolApprovalResult> ReviewApprovalAsync(ToolApprovalScope? scope, CancellationToken cancellationToken)
    {
        var previousPreview = await ChatContext.DispatchAsync(() =>
        {
            var previous = _activityPresentationSlot.Preview;
            _activityPresentationSlot.Preview = new ChatPluginTextActivityPreview(new DynamicLocaleKey(LocaleKey.ToolApproval_Reviewing));
            return previous;
        });
        try
        {
            var result = await _reviewAsync(this, scope, cancellationToken);
            HasApprovalDenied |= !result.IsAllowed;
            ApprovalFailure ??= result.Failure;
            if (!result.IsAllowed)
            {
                await ChatContext.DispatchAsync(() => FunctionCallChatMessage.ErrorMessageKey = result.Failure is null ?
                    new DynamicLocaleKey(LocaleKey.ConsentDecision_Deny) :
                    new FormattedDynamicLocaleKey(LocaleKey.ToolApproval_Failed, new DirectLocaleKey(result.FormatReason())));
            }

            return result;
        }
        finally
        {
            await SetActivityPreviewAsync(previousPreview);
        }
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
        await ChatContext.DispatchAsync(_activityPresentationSlot.EnterUserInputWait);
        try
        {
            return await interaction();
        }
        finally
        {
            await ChatContext.DispatchAsync(_activityPresentationSlot.ExitUserInputWait);
        }
    }

    /// <summary>
    /// Ends the invocation-scoped presentation lifetime. Removing the stable slot cannot clear or
    /// overwrite state owned by another invocation in the same aggregate message.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        return new ValueTask(
            ChatContext.DispatchAsync(() =>
                FunctionCallChatMessage.UnregisterActivityPresentation(InvocationId, _activityPresentationSlot)));
    }

    /// <inheritdoc />
    public Task SetActivityPreviewAsync(ChatPluginActivityPreview? preview)
    {
        return ChatContext.DispatchAsync(() => _activityPresentationSlot.Preview = preview);
    }

    /// <inheritdoc />
    public Task AppendBlockAsync(ChatPluginDisplayBlock block)
    {
        return ChatContext.DispatchAsync(() => FunctionCallChatMessage.DisplaySink.AppendBlock(block));
    }

    /// <inheritdoc />
    public Task AppendBlocksAsync(IEnumerable<ChatPluginDisplayBlock> blocks)
    {
        return ChatContext.DispatchAsync(() => FunctionCallChatMessage.DisplaySink.AppendBlocks(blocks));
    }
}