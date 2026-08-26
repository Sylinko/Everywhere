using Everywhere.ProcessIsolation.Hosts.Diagnostics;
using Everywhere.ProcessIsolation.Roles;
using Serilog;
using Serilog.Events;

namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>
/// Main-side endpoint for lightweight Host diagnostics. Main owns formatting,
/// persistence, and remote sinks so isolated roles do not load the logging graph.
/// </summary>
internal sealed class HostDiagnosticsLogSink(ProcessRole role) : IHostDiagnosticsRpc
{
    private readonly ILogger _logger = Log.ForContext<HostDiagnosticsLogSink>().ForContext("ProcessRole", ProcessRoleNames.ToWireName(role));

    /// <inheritdoc />
    public ValueTask LogAsync(HostLogNotification notification, CancellationToken cancellationToken = default)
    {
        var logger = _logger.ForContext("HostSource", notification.Source);
        if (notification.ExceptionText is not null)
        {
            logger = logger.ForContext("HostException", notification.ExceptionText);
        }

        logger.Write(ToSerilogLevel(notification.Level), "{HostMessage}", notification.Message);
        return ValueTask.CompletedTask;
    }

    private static LogEventLevel ToSerilogLevel(HostLogLevel level) => level switch
    {
        HostLogLevel.Debug => LogEventLevel.Debug,
        HostLogLevel.Information => LogEventLevel.Information,
        HostLogLevel.Warning => LogEventLevel.Warning,
        HostLogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Information
    };
}