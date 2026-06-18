using System.Globalization;

namespace Everywhere.Statistics;

/// <summary>
/// Selects whether statistics queries aggregate all known devices or only this installation.
/// </summary>
public enum StatisticsDeviceScope
{
    AllDevices,
    CurrentDevice
}

/// <summary>
/// Metrics supported by the home activity heatmap.
/// </summary>
public enum StatisticsHeatmapMetric
{
    Topics,
    Turns,
    Tokens,
    VisualContext,
    ToolUsage
}

/// <summary>
/// User-request events counted as turns.
/// </summary>
/// <remarks>
/// Continue is intentionally absent because it does not contain a new user request.
/// </remarks>
public enum StatisticsTurnKind
{
    Send,
    Edit,
    Retry
}

/// <summary>
/// Identifies why a model API request was made.
/// </summary>
public enum StatisticsModelInvocationPurpose
{
    ChatResponse,
    ContinueResponse,
    TopicGeneration,
    SubagentResponse,
    Backfill
}

/// <summary>
/// Final outcome of a tool function invocation.
/// </summary>
public enum StatisticsToolInvocationStatus
{
    Success,
    Denied,
    Disabled,
    NotFound,
    Error,
    Canceled
}

/// <summary>
/// Source that produced a visual context measurement.
/// </summary>
public enum StatisticsVisualContextSource
{
    AutomaticAttachmentProcessing,
    VisualContextPlugin,
    ScreenCapture,
    TextSelection,
    ImageAttachment,
    ToolResultContext
}

/// <summary>
/// Half-open query interval used by statistics readers: <c>[Start, End)</c>.
/// </summary>
/// <remarks>
/// Callers should pass boundaries already converted for the intended display timezone.
/// Stored events remain <see cref="DateTimeOffset"/> values.
/// </remarks>
public sealed record StatisticsRange(DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Aggregated token usage copied from model provider usage metadata.
/// </summary>
/// <remarks>
/// Visual context is not estimated into this summary; only provider-reported tokens are counted.
/// </remarks>
public sealed record StatisticsTokenSummary(
    long InputTokenCount,
    long CachedInputTokenCount,
    long OutputTokenCount,
    long ReasoningTokenCount,
    long TotalTokenCount
)
{
    /// <summary>
    /// Ratio of cached input tokens to all input tokens.
    /// </summary>
    public double CachedInputHitRate => InputTokenCount > 0 ? (double)CachedInputTokenCount / InputTokenCount : 0d;
}

/// <summary>
/// Top-level aggregates for the home dashboard overview cards.
/// </summary>
public sealed record StatisticsOverview(
    long TopicCount,
    long TurnCount,
    StatisticsTokenSummary TokenSummary,
    long VisualContextEventCount,
    long VisualElementCount,
    long ScreenshotCount,
    long ImageCount,
    long VisualContextBytes,
    long ToolInvocationCount
);

/// <summary>
/// Aggregated value for one local-calendar day in the activity heatmap.
/// </summary>
public interface IStatisticsHeatmapDay
{
    DateOnly Date { get; }

    long Value { get; }

    IDynamicResourceKey ToolTipKey { get; }
}

/// <summary>
/// Simple heatmap day used by count-like metrics.
/// </summary>
public sealed record StatisticsSimpleHeatmapDay(DateOnly Date, long Value) : IStatisticsHeatmapDay
{
    public IDynamicResourceKey ToolTipKey => new FormattedDynamicResourceKey(
        LocaleKey.HomePage_HeatmapDayToolTip,
        new DirectResourceKey(Date.ToString("D", CultureInfo.CurrentCulture)),
        new DirectResourceKey(Value.ToString("N0", CultureInfo.CurrentCulture)));
}

/// <summary>
/// Token heatmap day that exposes provider-reported token details through the tooltip.
/// </summary>
public sealed record StatisticsTokenHeatmapDay(
    DateOnly Date,
    long InputTokenCount,
    long OutputTokenCount,
    long CachedInputTokenCount
) : IStatisticsHeatmapDay
{
    public long Value => InputTokenCount + OutputTokenCount;

    public IDynamicResourceKey ToolTipKey => new FormattedDynamicResourceKey(
        LocaleKey.HomePage_HeatmapTokenDayToolTip,
        new DirectResourceKey(Date.ToString("D", CultureInfo.CurrentCulture)),
        new DirectResourceKey(InputTokenCount.ToString("N0", CultureInfo.CurrentCulture)),
        new DirectResourceKey(OutputTokenCount.ToString("N0", CultureInfo.CurrentCulture)),
        new DirectResourceKey(CachedInputTokenCount.ToString("N0", CultureInfo.CurrentCulture)));
}

/// <summary>
/// Current enabled/available capability counts from plugin and skill managers.
/// </summary>
/// <remarks>
/// This is live capability state, not historical usage.
/// </remarks>
public sealed record StatisticsCapabilitySummary(
    StatisticsCapabilityGroup Assistants,
    StatisticsCapabilityGroup Mcp,
    StatisticsCapabilityGroup BuiltInTools,
    StatisticsCapabilityGroup Skills
);

/// <summary>
/// Enabled and total counts for one capability category.
/// </summary>
public sealed record StatisticsCapabilityGroup(int EnabledCount, int TotalCount);

/// <summary>
/// Immutable input used when starting a model invocation event.
/// </summary>
public sealed record StatisticsModelInvocationDraft(
    Guid Id,
    Guid? TurnEventId,
    Guid? ChatContextId,
    Guid? AssistantChatNodeId,
    StatisticsModelInvocationPurpose Purpose,
    string? ModelId,
    DateTimeOffset StartedAt
);

/// <summary>
/// Immutable input used when starting a tool invocation event.
/// </summary>
/// <remarks>
/// Arguments and result payloads are deliberately excluded because they can contain sensitive data.
/// </remarks>
public sealed record StatisticsToolInvocationDraft(
    Guid Id,
    Guid? TurnEventId,
    Guid? ModelInvocationEventId,
    Guid? ChatContextId,
    string? PluginKey,
    string? FunctionName,
    DateTimeOffset StartedAt
);

/// <summary>
/// Quantitative visual context metrics to record.
/// </summary>
/// <remarks>
/// Keep this payload numeric-only. It should not contain visual tree XML, screenshots, OCR text, or user content.
/// </remarks>
public sealed record StatisticsVisualContextDraft(
    Guid? TurnEventId,
    Guid? ChatContextId,
    StatisticsVisualContextSource Source,
    int ElementCount = 0,
    int ScreenshotCount = 0,
    int ImageCount = 0,
    long ByteCount = 0
);
