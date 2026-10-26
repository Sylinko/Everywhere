using Everywhere.Chat.Plugins;
using Lucide.Avalonia;

namespace Everywhere.Chat;

/// <summary>
/// Message-level values captured for rendering, not persistence. The source retains identity;
/// presentation rows own commands and interaction state. Mutable descendants are captured separately.
/// </summary>
public abstract record ChatMessageState;

/// <summary>
/// User text and attachment membership captured for rendering.
/// </summary>
public record ChatUserState(string Content, IReadOnlyList<ChatAttachment> Attachments) : ChatMessageState;

/// <summary>
/// A strategy invocation retains the strategy label alongside the user text.
/// </summary>
public sealed record ChatUserStrategyState(
    string Content,
    IReadOnlyList<ChatAttachment> Attachments,
    IDynamicLocaleKey StrategyNameKey
) : ChatUserState(Content, Attachments);

/// <summary>
/// A user-triggered action's visible label.
/// </summary>
public sealed record ChatUserActionState(LucideIconKind Icon, IDynamicLocaleKey? HeaderKey) : ChatMessageState;

/// <summary>
/// A compaction attempt's current timeline status.
/// </summary>
public sealed record ChatCompressionState(IDynamicLocaleKey? HeaderKey, IDynamicLocaleKey? ErrorMessageKey, bool IsBusy) : ChatMessageState;

/// <summary>
/// A standalone action's captured status and duration.
/// </summary>
public sealed record ChatActionState(
    LucideIconKind Icon,
    IDynamicLocaleKey? HeaderKey,
    IDynamicLocaleKey? ErrorMessageKey,
    bool IsBusy,
    double ElapsedSeconds
) : ChatMessageState;

/// <summary>
/// Captured function activity values. Display-block membership is a stable reference snapshot;
/// expanded block presenters capture their own mutable values.
/// </summary>
public sealed record ChatFunctionState(
    LucideIconKind Icon,
    IDynamicLocaleKey? HeaderKey,
    string? Content,
    ChatPluginActivityPreview? ActivityPreview,
    IDynamicLocaleKey? ErrorMessageKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    bool IsRunning,
    bool IsWaitingForUserInput,
    IReadOnlyList<ChatPluginDisplayBlock> DisplayBlocks
) : ChatMessageState;

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
    double TokensPerSecond,
    double? CachedInputHitRate
);