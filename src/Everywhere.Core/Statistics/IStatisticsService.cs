namespace Everywhere.Statistics;

/// <summary>
/// Read-side API for home dashboard statistics.
/// </summary>
public interface IStatisticsService
{
    /// <summary>
    /// Gets card-level aggregates for a half-open time range.
    /// </summary>
    Task<StatisticsOverview> GetOverviewAsync(
        StatisticsRange range,
        StatisticsDeviceScope deviceScope = StatisticsDeviceScope.AllDevices,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets local-calendar day values for the requested heatmap metric.
    /// </summary>
    /// <param name="metric"></param>
    /// <param name="months">Number of months to include, clamped by the implementation.</param>
    /// <param name="deviceScope"></param>
    /// <param name="cancellationToken"></param>
    Task<IReadOnlyList<IStatisticsHeatmapDay>> GetHeatmapAsync(
        StatisticsHeatmapMetric metric,
        int months,
        StatisticsDeviceScope deviceScope = StatisticsDeviceScope.AllDevices,
        CancellationToken cancellationToken = default);
}