using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Microsoft.Extensions.AI;

namespace Everywhere.Harness;

/// <summary>
/// Represents the ambient state of one concrete tool invocation.
/// </summary>
/// <remarks>
/// Each live call owns its message, ambient services, and activity preview. Older
/// messages can contain several same-name calls; they remain readable as recorded.
/// </remarks>
public sealed class FunctionCallContext : IChatPluginExecutionContext, IChatPluginUserInterface, IChatPluginDisplaySink, IAsyncDisposable
{
    private static readonly AsyncLocal<FunctionCallContext?> SCurrent = new();

    /// <summary>
    /// Gets the ambient tool invocation in the current async execution flow, including after foreground detach.
    /// </summary>
    public static FunctionCallContext? Current => SCurrent.Value;

    public IServiceProvider Services { get; }

    public ChatContext ChatContext { get; }

    public ToolBinding Binding { get; }

    public ChatPluginSnapshot ChatPlugin => Binding.Plugin;

    public ChatFunction ChatFunction => Binding.Function;

    public FunctionCallChatMessage FunctionCallChatMessage { get; }

    public FunctionCallContent FunctionCallContent { get; }

    public GenerationContext GenerationContext { get; }

    /// <inheritdoc />
    public string InvocationId { get; }

    /// <inheritdoc />
    public ToolApprovalMode ApprovalMode { get; }

    /// <summary>
    /// Gets whether a required consent was denied during this invocation.
    /// </summary>
    public bool HasApprovalDenied { get; private set; }

    /// <summary>
    /// Gets the latest automatic-review failure for invocation outcome reporting.
    /// </summary>
    public ToolApprovalFailure? ApprovalFailure { get; private set; }

    public ObservableToolRulesets ToolBypassApprovalRulesets { get; }

    public IChatPluginDisplaySink DisplaySink => this;

    /// <summary>
    /// Gets whether this tool invocation supports moving from foreground execution to background execution.
    /// </summary>
    public bool SupportsBackgrounding { get; }

    /// <inheritdoc />
    public bool IsBackgrounded => Volatile.Read(ref _detachState) == DetachStateDetached;

    /// <summary>
    /// Gets the execution-scoped cancellation token active for this invocation (still linked after background detach).
    /// </summary>
    public CancellationToken ExecutionCancellationToken { get; }

    /// <summary>
    /// Gets whether the current function call may bypass approval.
    /// </summary>
    /// <remarks>
    /// Path-scoped approvals are evaluated separately by file-system operations and do not alter this value.
    /// </remarks>
    public bool BypassesApproval => ApprovalMode == ToolApprovalMode.FullAccess ||
        ToolBypassApprovalPolicy.BypassesApproval(ToolBypassApprovalRulesets, ChatPlugin, ChatFunction);

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

    /// <summary>
    /// Gets a task that completes when this invocation has moved to background execution.
    /// </summary>
    public Task BackgroundMovedTask => _backgroundMovedTcs.Task;

    private const int DetachStateForeground = 0;
    private const int DetachStateDetached = 1;
    private const int DetachStateForegroundCanceled = 2;
    private const int DetachStateForegroundCompleted = 3;

    private readonly FunctionCallChatMessage.ActivityPresentationSlot _activityPresentationSlot;
    private readonly Func<FunctionCallContext, ToolApprovalScope?, CancellationToken, Task<ToolApprovalResult>> _reviewAsync;
    private readonly TaskCompletionSource _backgroundMovedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _detachState;

    /// <summary>
    /// Creates the invocation and registers its presentation slot inside the root execution domain.
    /// The caller awaits disposal before completing the enclosing tool operation.
    /// </summary>
    public FunctionCallContext(
        IServiceProvider services,
        ChatContext chatContext,
        ToolBinding binding,
        FunctionCallChatMessage functionCallChatMessage,
        FunctionCallContent functionCallContent,
        ObservableToolRulesets toolBypassApprovalRulesets,
        GenerationContext generationContext,
        Func<FunctionCallContext, ToolApprovalScope?, CancellationToken, Task<ToolApprovalResult>> reviewAsync,
        bool supportsBackgrounding = false,
        CancellationToken executionCancellationToken = default)
    {
        if (functionCallContent.CallId.IsNullOrEmpty())
        {
            throw new ArgumentException("A function call context requires a non-empty tool-call ID.", nameof(functionCallContent));
        }

        Services = services;
        ChatContext = chatContext;
        Binding = binding;
        FunctionCallChatMessage = functionCallChatMessage;
        FunctionCallContent = functionCallContent;
        GenerationContext = generationContext;
        ApprovalMode = generationContext.ApprovalMode;
        _reviewAsync = reviewAsync;
        SupportsBackgrounding = supportsBackgrounding;
        ExecutionCancellationToken = executionCancellationToken;
        InvocationId = functionCallContent.CallId;
        ToolBypassApprovalRulesets = toolBypassApprovalRulesets;
        _activityPresentationSlot = functionCallChatMessage.RegisterActivityPresentation(InvocationId);
    }

    internal static IDisposable EnterCurrent(FunctionCallContext context)
    {
        var previous = SCurrent.Value;
        SCurrent.Value = context;
        return Disposable.Create(() => SCurrent.Value = previous);
    }

    /// <summary>
    /// Suppresses both <see cref="Current"/> and <paramref name="generationContext"/> ambient invocation while a nested context initializes.
    /// </summary>
    public static IDisposable SuppressCurrent(GenerationContext? generationContext = null)
    {
        var previousStatic = SCurrent.Value;
        SCurrent.Value = null;
        if (generationContext is null)
        {
            return Disposable.Create(() => SCurrent.Value = previousStatic);
        }

        var previousGen = generationContext.FunctionCallContext.Value;
        generationContext.FunctionCallContext.Value = null;
        return Disposable.Create(() =>
        {
            generationContext.FunctionCallContext.Value = previousGen;
            SCurrent.Value = previousStatic;
        });
    }

    /// <inheritdoc />
    public bool TryMoveToBackground()
    {
        if (!SupportsBackgrounding ||
            Interlocked.CompareExchange(ref _detachState, DetachStateDetached, DetachStateForeground) != DetachStateForeground)
        {
            return false;
        }

        _backgroundMovedTcs.TrySetResult();
        return true;
    }

    internal bool TryCancelForeground() =>
        Interlocked.CompareExchange(ref _detachState, DetachStateForegroundCanceled, DetachStateForeground) == DetachStateForeground;

    internal bool TryCompleteForeground() =>
        Interlocked.CompareExchange(ref _detachState, DetachStateForegroundCompleted, DetachStateForeground) == DetachStateForeground;

    /// <inheritdoc />
    public IDisposable BeginForegroundBudget(TimeSpan budget)
    {
        if (!SupportsBackgrounding || IsBackgrounded || budget == Timeout.InfiniteTimeSpan)
        {
            return Disposable.Empty;
        }

        if (budget <= TimeSpan.Zero)
        {
            TryMoveToBackground();
            return Disposable.Empty;
        }

        var timerCts = new CancellationTokenSource(budget);
        var registration = timerCts.Token.Register(() => TryMoveToBackground());
        return Disposable.Create(() =>
        {
            registration.Dispose();
            timerCts.Dispose();
        });
    }

    /// <summary>
    /// Removes the foreground card's presentation slot after moving to background without altering shared broker approvals.
    /// </summary>
    internal Task DetachPresentationAsync()
    {
        return ChatContext.DispatchAsync(() =>
            FunctionCallChatMessage.UnregisterActivityPresentation(InvocationId, _activityPresentationSlot));
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
            var review = await ReviewApprovalAsync(approvalScope, cancellationToken.CanBeCanceled ? cancellationToken : ExecutionCancellationToken);
            return review.IsAllowed ? RequestConsentResult.Accept : RequestConsentResult.Deny(review.FormatReason());
        }

        var effectiveToken = cancellationToken.CanBeCanceled ? cancellationToken : ExecutionCancellationToken;
        var consentDecision = await WaitForUserInputAsync(() => ChatContext.UserInterfaceBroker.HandleConsentRequestAsync(
            headerKey,
            content,
            rememberMasks,
            customOptions,
            effectiveToken));

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
        var effectiveToken = cancellationToken.CanBeCanceled ? cancellationToken : ExecutionCancellationToken;
        return await WaitForUserInputAsync(() => ChatContext.UserInterfaceBroker.HandleAskQuestionAsync(questions, effectiveToken));
    }

    #endregion

    /// <summary>
    /// Reviews this invocation's required consent without entering human-input wait state.
    /// </summary>
    public async Task<ToolApprovalResult> ReviewApprovalAsync(ToolApprovalScope? scope, CancellationToken cancellationToken)
    {
        var shouldUpdatePreview = !IsBackgrounded;
        ChatPluginActivityPreview? previousPreview = null;
        if (shouldUpdatePreview)
        {
            previousPreview = await ChatContext.DispatchAsync(() =>
            {
                var previous = _activityPresentationSlot.Preview;
                _activityPresentationSlot.Preview = new ChatPluginTextActivityPreview(new DynamicLocaleKey(LocaleKey.ToolApproval_Reviewing));
                return previous;
            });
        }
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
            if (shouldUpdatePreview && !IsBackgrounded)
            {
                await SetActivityPreviewAsync(previousPreview);
            }
        }
    }

    /// <summary>
    /// Runs an interaction inside this invocation's transient user-input wait state.
    /// </summary>
    public async Task<T> WaitForUserInputAsync<T>(Func<Task<T>> interaction)
    {
        var enteredWait = false;
        if (!IsBackgrounded)
        {
            enteredWait = true;
            await ChatContext.DispatchAsync(_activityPresentationSlot.EnterUserInputWait);
        }

        try
        {
            return await interaction();
        }
        finally
        {
            if (enteredWait)
            {
                await ChatContext.DispatchAsync(_activityPresentationSlot.ExitUserInputWait);
            }
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
        if (IsBackgrounded) return Task.CompletedTask;
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