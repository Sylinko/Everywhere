using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Everywhere.Terminal;

/// <summary>
/// OpenTelemetry tracing and metrics instruments for terminal strategy detection and command execution.
/// </summary>
internal static class TerminalTelemetry
{
    public static ActivitySource ActivitySource { get; } = new("Everywhere.Terminal");

    public static Meter Meter { get; } = new("Everywhere.Terminal");

    public static Counter<long> StrategyDetections { get; } =
        Meter.CreateCounter<long>("terminal.strategy.detections", description: "Count of shell integration strategy detections.");

    public static Histogram<double> StrategyDetectDuration { get; } =
        Meter.CreateHistogram<double>("terminal.strategy.detect.duration", "s", "Duration of shell integration strategy detection.");

    public static Counter<long> RichDegradations { get; } =
        Meter.CreateCounter<long>("terminal.rich.degradations", description: "Count of runtime degradations within Rich execution strategy.");

    public static Counter<long> Executions { get; } =
        Meter.CreateCounter<long>("terminal.executions", description: "Count of terminal command executions.");

    public static Histogram<double> ExecutionDuration { get; } =
        Meter.CreateHistogram<double>("terminal.execution.duration", "s", "Duration of terminal command execution.");

    public static string NormalizeErrorType(string? errorType)
    {
        if (string.IsNullOrWhiteSpace(errorType)) return "none";
        if (errorType.StartsWith("LanguageMode:", StringComparison.Ordinal)) return errorType;

        var lastDot = errorType.LastIndexOf('.');
        return lastDot >= 0 && lastDot + 1 < errorType.Length
            ? errorType[(lastDot + 1)..]
            : errorType;
    }
}
