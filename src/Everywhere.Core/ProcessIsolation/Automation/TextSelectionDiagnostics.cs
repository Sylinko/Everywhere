using System.Diagnostics;
using System.Globalization;
using Everywhere.Automation;
using Everywhere.Common;
using Everywhere.ProcessIsolation.Hosts.Diagnostics;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>
/// Reports one monitor's failures to Main without exposing selected text, provider messages or paths.
/// Detection never waits for delivery, and the Host does not initialize Main's logging services.
/// </summary>
public sealed class TextSelectionDiagnostics(IHostDiagnosticsRpc diagnostics, long contextId, long monitorId)
{
    /// <summary>
    /// Reports a failed operation in the background. The phase must be a fixed diagnostic label,
    /// not application data. Transport failures are ignored without producing another log entry.
    /// </summary>
    public void ReportFailure(HostLogLevel level, string phase, Exception exception)
    {
        var failureKind = AutomationRpcExceptionMapping.TryGetVisualElementQueryFailureKind(exception, out var kind) ?
            kind :
            (VisualElementQueryFailureKind?)null;
        ReportFailureAsync(level, phase, exception, failureKind).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    /// <summary>
    /// Reports a normalized query failure when no successful selection will carry it to Main.
    /// </summary>
    public void ReportFailure(HostLogLevel level, string phase, VisualElementQueryFailure failure)
    {
        ReportFailureAsync(level, phase, failure.Exception, failure.Kind).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    private async Task ReportFailureAsync(
        HostLogLevel level,
        string phase,
        Exception? exception,
        VisualElementQueryFailureKind? failureKind)
    {
        // Normalized provider exceptions may wrap the original COM/AX failure. Preserve its
        // type and HRESULT, but never Exception.Message, Data or ToString(): those can contain text.
        var properties = new Dictionary<string, string>
        {
            ["Phase"] = phase,
            ["ContextId"] = contextId.ToString(CultureInfo.InvariantCulture),
            ["MonitorId"] = monitorId.ToString(CultureInfo.InvariantCulture),
        };
        var stackTrace = default(string);
        if (exception is not null)
        {
            var cause = exception.GetBaseException();
            properties["ExceptionType"] = exception.GetType().FullName ?? exception.GetType().Name;
            properties["CauseType"] = cause.GetType().FullName ?? cause.GetType().Name;
            properties["HResult"] = cause.HResult.ToString("X8", CultureInfo.InvariantCulture);
            stackTrace = new StackTrace(cause, false).ToString();
        }

        if (failureKind is { } kind)
        {
            properties["FailureKind"] = kind.ToString();
        }

        await diagnostics.LogAsync(new HostLogNotification
        {
            Level = level,
            Source = nameof(TextSelectionDiagnostics),
            Message = "Text-selection operation failed.",
            // File information is deliberately excluded; method frames remain useful for diagnosis.
            ExceptionText = stackTrace,
            Properties = properties,
        }).ConfigureAwait(false);
    }
}