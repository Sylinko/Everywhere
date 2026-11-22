using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Everywhere.Terminal;

/// <summary>
/// Strategy for executing commands in a PTY and capturing their output.
/// Implementations differ based on whether Shell Integration is available.
/// </summary>
public abstract partial class ExecuteStrategy
{
    private static readonly TimeSpan DefaultDetectAbsoluteTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultDetectIdleTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Detect whether Shell Integration is available by reading initial PTY output
    /// and checking for OSC 633 markers. Returns the appropriate strategy.
    /// </summary>
    public static Task<ExecuteStrategy> DetectStrategyAsync(
        TerminalSession session,
        ShellType shellType,
        ILogger logger,
        CancellationToken cancellationToken = default,
        string? shellPath = null
    ) =>
        DetectStrategyAsync(
            session,
            shellType,
            logger,
            DefaultDetectIdleTimeout,
            DefaultDetectAbsoluteTimeout,
            shellPath,
            cancellationToken);

    internal static async Task<ExecuteStrategy> DetectStrategyAsync(
        TerminalSession session,
        ShellType shellType,
        ILogger logger,
        TimeSpan idleTimeout,
        TimeSpan absoluteTimeout,
        string? shellPath = null,
        CancellationToken cancellationToken = default
    )
    {
        var shellKind = ShellIntegrationScript.GetShellKind(shellType, shellPath ?? session.ShellPath);
        // ReSharper disable once ExplicitCallerInfoArgument
        using var activity = TerminalTelemetry.ActivitySource.StartActivity("terminal.detect_strategy");
        activity?.SetTag("terminal.shell.kind", shellKind);
        activity?.SetTag("terminal.shell.type", shellType.ToString());

        var startTimestamp = Stopwatch.GetTimestamp();
        var initialBytesRead = session.BytesRead;
        var hasPromptStart = false;
        var hasCommandReady = false;
        string? integrationError = null;
        var seenMarkers = new List<char>(4);

        void HandleMarker(in ShellIntegrationMarker marker)
        {
            var code = marker.Type switch
            {
                ShellIntegrationMarkerType.PromptStart => 'A',
                ShellIntegrationMarkerType.CommandReady => 'B',
                ShellIntegrationMarkerType.CommandExecuted => 'C',
                ShellIntegrationMarkerType.CommandFinished => 'D',
                ShellIntegrationMarkerType.CommandLine => 'E',
                ShellIntegrationMarkerType.Property => 'P',
                _ => '?'
            };
            if (!seenMarkers.Contains(code)) seenMarkers.Add(code);

            switch (marker.Type)
            {
                case ShellIntegrationMarkerType.PromptStart:
                    hasPromptStart = true;
                    break;
                case ShellIntegrationMarkerType.CommandReady:
                    hasCommandReady = true;
                    break;
                case ShellIntegrationMarkerType.Property when
                    marker.Property is { } prop &&
                    prop.StartsWith("IntegrationError=", StringComparison.Ordinal):
                    integrationError = prop["IntegrationError=".Length..];
                    break;
            }
        }

        LogDetectStartingShellIntegrationDetection(
            logger,
            shellKind,
            shellType,
            idleTimeout.TotalSeconds,
            absoluteTimeout.TotalSeconds);

        string reason;
        session.Parser.ShellIntegrationMarkerReceived += HandleMarker;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remainingAbsolute = absoluteTimeout - Stopwatch.GetElapsedTime(startTimestamp);
                if (remainingAbsolute <= TimeSpan.Zero)
                {
                    reason = "absolute_timeout";
                    break;
                }

                var readTimeout = remainingAbsolute < idleTimeout ? remainingAbsolute : idleTimeout;
                var outcome = await session.ReadOrIdleAsync(readTimeout, cancellationToken);
                if (hasCommandReady)
                {
                    reason = "command_ready";
                    break;
                }

                if (integrationError is not null)
                {
                    reason = "integration_error";
                    break;
                }

                if (outcome == TerminalReadOutcome.EndOfStream)
                {
                    reason = "eof";
                    break;
                }

                if (outcome == TerminalReadOutcome.Idle)
                {
                    if (readTimeout < remainingAbsolute && (session.BytesRead == initialBytesRead || hasPromptStart))
                    {
                        // Still waiting for cold-start first byte or slow prompt completion after OSC 633;A.
                        continue;
                    }

                    reason = readTimeout == remainingAbsolute ? "absolute_timeout" : "idle_timeout";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            RecordDetectionTelemetry("none", "canceled", "OperationCanceledException");
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordDetectionTelemetry("none", "exception", ex.GetType().Name);
            throw;
        }
        finally
        {
            session.Parser.ShellIntegrationMarkerReceived -= HandleMarker;
        }

        var errorType = TerminalTelemetry.NormalizeErrorType(integrationError);
        var strategyName = hasCommandReady ? "rich" : "none";
        var (elapsed, bytesRead, seenMarkersText) = RecordDetectionTelemetry(strategyName, reason, errorType, integrationError);

        if (hasCommandReady)
        {
            LogDetectShellIntegrationCommandReadyDetected(
                logger,
                shellKind,
                shellType,
                elapsed.TotalMilliseconds);
            return new RichExecuteStrategy(logger);
        }

        LogDetectFallingBackToNoneStrategy(
            logger,
            shellKind,
            shellType,
            elapsed.TotalMilliseconds,
            reason,
            integrationError ?? "none",
            bytesRead,
            seenMarkersText,
            session.Parser.IsBracketedPasteModeEnabled);

        return new NoneExecuteStrategy(logger);

        (TimeSpan Elapsed, long BytesRead, string SeenMarkersText) RecordDetectionTelemetry(
            string recordedStrategy,
            string recordedReason,
            string recordedErrorType,
            string? fullError = null)
        {
            var detectElapsed = Stopwatch.GetElapsedTime(startTimestamp);
            var detectBytesRead = session.BytesRead - initialBytesRead;
            var markers = seenMarkers.Count == 0 ? "none" : new string(seenMarkers.ToArray());

            activity?.SetTag("terminal.strategy", recordedStrategy);
            activity?.SetTag("terminal.detect.reason", recordedReason);
            activity?.SetTag("terminal.detect.bytes_read", detectBytesRead);
            activity?.SetTag("terminal.detect.markers_seen", markers);
            if (recordedErrorType != "none")
            {
                activity?.SetTag("error.type", recordedErrorType);
            }
            if (fullError is not null)
            {
                activity?.SetTag("terminal.detect.error", fullError);
            }

            TerminalTelemetry.StrategyDetections.Add(
                1,
                new TagList
                {
                    { "shell.kind", shellKind },
                    { "strategy", recordedStrategy },
                    { "reason", recordedReason },
                    { "error.type", recordedErrorType }
                });
            TerminalTelemetry.StrategyDetectDuration.Record(
                detectElapsed.TotalSeconds,
                new TagList
                {
                    { "shell.kind", shellKind },
                    { "strategy", recordedStrategy }
                });

            return (detectElapsed, detectBytesRead, markers);
        }
    }

    /// <summary>
    /// Start executing a command in the PTY and stream the discovered terminal runs.
    /// </summary>
    /// <param name="session">The terminal session to use.</param>
    /// <param name="script">The script to execute.</param>
    /// <param name="shellType"></param>
    /// <param name="timeout">Optional maximum time to wait for the command to complete. Null or infinite waits until cancellation or completion.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The terminal runs discovered while the command is executing.</returns>
    public async IAsyncEnumerable<TerminalRun> ExecuteAsync(
        TerminalSession session,
        string script,
        ShellType shellType,
        TimeSpan? timeout = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var channel = Channel.CreateUnbounded<TerminalRun>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });

        var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scope = CreateExecutionScope(channel.Writer, session, script, shellType, timeout, linkedCancellationTokenSource.Token);
        var scopeTask = RunScopeAsync();

        try
        {
            await foreach (var run in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return run;
            }
        }
        finally
        {
            await linkedCancellationTokenSource.CancelAsync();

            try
            {
                await scopeTask;
            }
            catch (OperationCanceledException) when (linkedCancellationTokenSource.IsCancellationRequested)
            {
                // Ignore
            }
        }

        async Task RunScopeAsync()
        {
            try
            {
                await scope.ExecuteAsync();
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }
    }

    protected abstract IExecutionScope CreateExecutionScope(
        ChannelWriter<TerminalRun> writer,
        TerminalSession session,
        string script,
        ShellType shellType,
        TimeSpan? timeout,
        CancellationToken cancellationToken
    );

    protected interface IExecutionScope
    {
        Task ExecuteAsync();
    }

    [LoggerMessage(LogLevel.Debug, "[Detect] Starting Shell Integration detection for {ShellKind} ({ShellType}) (Idle: {Idle}s, Max: {Max}s)")]
    static partial void LogDetectStartingShellIntegrationDetection(
        ILogger logger,
        string shellKind,
        ShellType shellType,
        double idle,
        double max
    );

    [LoggerMessage(
        LogLevel.Debug,
        "[Detect] Shell Integration command-ready detected for {ShellKind} ({ShellType}) in {ElapsedMs:F0}ms, using Rich strategy")]
    static partial void LogDetectShellIntegrationCommandReadyDetected(
        ILogger logger,
        string shellKind,
        ShellType shellType,
        double elapsedMs
    );

    [LoggerMessage(
        LogLevel.Warning,
        "[Detect] Falling back to None strategy for {ShellKind} ({ShellType}) after {ElapsedMs:F0}ms: reason={Reason}, integrationError={IntegrationError}, bytesRead={BytesRead}, seenMarkers={SeenMarkers}, bracketedPaste={IsBracketedPasteModeEnabled}")]
    static partial void LogDetectFallingBackToNoneStrategy(
        ILogger logger,
        string shellKind,
        ShellType shellType,
        double elapsedMs,
        string reason,
        string integrationError,
        long bytesRead,
        string seenMarkers,
        bool isBracketedPasteModeEnabled
    );
}
