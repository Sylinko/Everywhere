using Everywhere.Chat.Plugins;
using Tabler.Avalonia;

namespace Everywhere.Chat;

/// <summary>
/// Message-level values captured for rendering, not persistence. The source retains identity;
/// presentation rows own commands and interaction state. Mutable descendants are captured separately.
/// </summary>
public abstract record ChatMessageState;

/// <summary>
/// User text and attachment membership captured for rendering.
/// </summary>
public record ChatUserState(
    string Content,
    IReadOnlyList<ChatAttachment> Attachments,
    ChatInputDeliveryMode? DeliveryMode = null
) : ChatMessageState
{
    public bool IsSteered => DeliveryMode == ChatInputDeliveryMode.Steer;

    public bool IsQueued => DeliveryMode == ChatInputDeliveryMode.Queue;
}

/// <summary>
/// A strategy invocation retains the strategy label alongside the user text.
/// </summary>
public sealed record ChatUserStrategyState(
    string Content,
    IReadOnlyList<ChatAttachment> Attachments,
    IDynamicLocaleKey StrategyNameKey,
    ChatInputDeliveryMode? DeliveryMode = null
) : ChatUserState(Content, Attachments, DeliveryMode);

/// <summary>
/// A completed background job notification rendered as an expandable timeline divider.
/// </summary>
public sealed record ChatBackgroundJobNotificationState(
    string JobId,
    string ToolName,
    string Label,
    bool IsSuccess,
    string Output,
    string? ErrorMessage
) : ChatMessageState
{
    public bool HasDetails => !string.IsNullOrWhiteSpace(Output) || !string.IsNullOrWhiteSpace(ErrorMessage);

    public string DisplayOutput => !IsSuccess && !string.IsNullOrWhiteSpace(ErrorMessage) ?
        $"{ErrorMessage}\n{Output}".Trim() :
        Output;

    public IDynamicLocaleKey StatusKey => IsSuccess ?
        new DynamicLocaleKey(LocaleKey.BackgroundJobNotification_Completed) :
        new DynamicLocaleKey(LocaleKey.BackgroundJobNotification_Failed);
}

/// <summary>
/// A user-triggered action's visible label.
/// </summary>
public sealed record ChatUserActionState(TablerIconKind Icon, IDynamicLocaleKey? HeaderKey) : ChatMessageState;

/// <summary>
/// A compaction attempt's current timeline status.
/// </summary>
public sealed record ChatCompressionState(IDynamicLocaleKey? HeaderKey, IDynamicLocaleKey? ErrorMessageKey, bool IsBusy) : ChatMessageState;

/// <summary>
/// A standalone action's captured status and duration.
/// </summary>
public sealed record ChatActionState(
    TablerIconKind Icon,
    IDynamicLocaleKey? HeaderKey,
    IDynamicLocaleKey? ErrorMessageKey,
    bool IsBusy,
    double ElapsedSeconds
) : ChatMessageState;

/// <summary>
/// Captured function activity values. Display blocks and image results keep their source identities;
/// expanded block presenters capture their own mutable values.
/// </summary>
public sealed record ChatFunctionState(
    TablerIconKind Icon,
    IDynamicLocaleKey? HeaderKey,
    string? Content,
    ChatPluginActivityPreview? ActivityPreview,
    IDynamicLocaleKey? ErrorMessageKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    bool IsRunning,
    bool IsWaitingForUserInput,
    IReadOnlyList<ChatPluginDisplayBlock> DisplayBlocks,
    IReadOnlyList<ViewImageFunctionResult> ImageViews
) : ChatMessageState
{
    /// <summary>
    /// Gets whether the call has anything to present when expanded.
    /// </summary>
    public bool HasDetails => ErrorMessageKey is not null || DisplayBlocks.Count > 0 || ImageViews.Count > 0;
}

/// <summary>
/// Captured assistant outcome and usage values. Spans are read independently.
/// </summary>
public sealed record ChatAssistantState(
    bool IsBusy,
    DateTimeOffset CreatedAt,
    DateTimeOffset FinishedAt,
    IDynamicLocaleKey? ErrorMessageKey,
    Exception? Error,
    ChatUsageState UsageDetails
) : ChatMessageState;

/// <summary>
/// Usage values captured for one render pass.
/// </summary>
public readonly record struct ChatUsageState(
    long InputTokenCount,
    long OutputTokenCount,
    long TotalTokenCount,
    double? TokensPerSecond,
    double? CachedInputHitRate
);