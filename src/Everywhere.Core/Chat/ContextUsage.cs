using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Chat;

public enum ContextUsageKind
{
    Unavailable,
    ProviderReported,
    Estimated
}

public enum ContextUsageUnavailableReason
{
    None,
    NotMeasured,
    ProviderDidNotReportUsage,
    CompactedAwaitingMeasurement
}

public sealed record ContextUsageSnapshot(
    ContextUsageKind Kind,
    ContextUsageUnavailableReason UnavailableReason,
    long? InputTokenCount,
    long? CachedInputTokenCount,
    long? OutputTokenCount,
    long? ReasoningTokenCount,
    long? TotalTokenCount,
    int? ContextLimit,
    string? ModelId,
    DateTimeOffset? UpdatedAt
)
{
    /// <summary>
    /// Default automatic compression threshold used when no custom assistant policy is available.
    /// </summary>
    public const int DefaultCompressionThresholdPercentage = 80;

    /// <summary>Lowest accepted automatic compression threshold percentage.</summary>
    public const int MinimumCompressionThresholdPercentage = 5;

    /// <summary>Highest accepted automatic compression threshold percentage.</summary>
    public const int MaximumCompressionThresholdPercentage = 95;

    public static ContextUsageSnapshot NotMeasured { get; } = new(
        Kind: ContextUsageKind.Unavailable,
        UnavailableReason: ContextUsageUnavailableReason.NotMeasured,
        InputTokenCount: null,
        CachedInputTokenCount: null,
        OutputTokenCount: null,
        ReasoningTokenCount: null,
        TotalTokenCount: null,
        ContextLimit: null,
        ModelId: null,
        UpdatedAt: null);

    public bool HasUsage => TotalTokenCount.HasValue;

    public bool HasContextLimit => ContextLimit is > 0;

    public bool HasUsageRatio => HasUsage && HasContextLimit;

    public bool HasUsageWithoutContextLimit => HasUsage && !HasContextLimit;

    /// <summary>
    /// Gets a value indicating whether the latest measurement has reached the supplied automatic
    /// compression threshold.
    /// </summary>
    public bool HasReachedCompressionThreshold(int thresholdPercentage) =>
        HasUsageRatio && UsageRatioValue >= NormalizeCompressionThresholdPercentage(thresholdPercentage) / 100d;

    public bool IsCompactedAwaitingMeasurement =>
        UnavailableReason == ContextUsageUnavailableReason.CompactedAwaitingMeasurement;

    public bool ShouldShowUnavailableMessage => !HasUsage && !IsCompactedAwaitingMeasurement;

    public double UsageRatio => HasUsageRatio ? Math.Clamp(UsageRatioValue, 0d, 1d) : 0d;

    public int UsagePercentage => HasUsageRatio ? (int)Math.Round(UsageRatioValue * 100d) : 0;

    private double UsageRatioValue => (double)TotalTokenCount.GetValueOrDefault() / ContextLimit.GetValueOrDefault();

    public static int NormalizeCompressionThresholdPercentage(int thresholdPercentage) =>
        Math.Clamp(thresholdPercentage, MinimumCompressionThresholdPercentage, MaximumCompressionThresholdPercentage);
}

/// <summary>
/// Stable, non-persisted binding surface for the most recent provider context measurement.
/// </summary>
/// <remarks>
/// The runtime publishes immutable snapshots without waiting for a view. UI subscribers marshal
/// notifications at their own boundary; shutdown does not depend on rendering the final state.
/// </remarks>
public sealed partial class ContextUsageState : ObservableObject
{
    [ObservableProperty]
    public partial ContextUsageSnapshot Snapshot { get; private set; } = ContextUsageSnapshot.NotMeasured;

    internal void Report(ChatUsageDetails usage, string? modelId, int contextLimit)
    {
        Snapshot = usage.HasUsage ?
            new ContextUsageSnapshot(
                ContextUsageKind.ProviderReported,
                ContextUsageUnavailableReason.None,
                usage.InputTokenCount,
                usage.CachedInputTokenCount,
                usage.OutputTokenCount,
                usage.ReasoningTokenCount,
                usage.TotalTokenCount,
                contextLimit > 0 ? contextLimit : null,
                modelId,
                DateTimeOffset.UtcNow) :
            new ContextUsageSnapshot(
                ContextUsageKind.Unavailable,
                ContextUsageUnavailableReason.ProviderDidNotReportUsage,
                null,
                null,
                null,
                null,
                null,
                contextLimit > 0 ? contextLimit : null,
                modelId,
                DateTimeOffset.UtcNow);
    }

    internal void UpdateModel(string? modelId, int contextLimit)
    {
        int? normalizedContextLimit = contextLimit > 0 ? contextLimit : null;
        if (!Snapshot.HasUsage)
        {
            Snapshot = Snapshot with { ContextLimit = normalizedContextLimit, ModelId = modelId };
            return;
        }

        Snapshot = Snapshot with
        {
            Kind = Snapshot.ModelId == modelId && Snapshot.ContextLimit == normalizedContextLimit ?
                Snapshot.Kind :
                ContextUsageKind.Estimated,
            ContextLimit = normalizedContextLimit,
            ModelId = modelId
        };
    }

    internal void MarkCompacted(string? modelId, int contextLimit)
    {
        Snapshot = new ContextUsageSnapshot(
            ContextUsageKind.Unavailable,
            ContextUsageUnavailableReason.CompactedAwaitingMeasurement,
            null,
            null,
            null,
            null,
            null,
            contextLimit > 0 ? contextLimit : null,
            modelId,
            DateTimeOffset.UtcNow);
    }
}

public enum ContextCompactionPhase
{
    Idle,
    Running
}

/// <summary>
/// Observable state for the current context compression operation.
/// </summary>
/// <remarks>
/// The runtime publishes immutable snapshots without waiting for a view. UI subscribers marshal
/// notifications at their own boundary; shutdown does not depend on rendering the final state.
/// </remarks>
public sealed partial class ContextCompactionState : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    public partial ContextCompactionPhase Phase { get; private set; }

    public bool IsRunning => Phase == ContextCompactionPhase.Running;

    internal void Start() => Phase = ContextCompactionPhase.Running;

    internal void Finish() => Phase = ContextCompactionPhase.Idle;
}