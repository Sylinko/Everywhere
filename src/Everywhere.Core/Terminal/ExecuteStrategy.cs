using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Everywhere.Terminal;

/// <summary>
/// Strategy for executing commands in a PTY and capturing their output.
/// Implementations differ based on whether Shell Integration is available.
/// </summary>
public abstract class ExecuteStrategy
{
    /// <summary>
    /// Detect whether Shell Integration is available by reading initial PTY output
    /// and checking for OSC 633 markers. Returns the appropriate strategy.
    /// </summary>
    public static async Task<ExecuteStrategy> DetectStrategyAsync(
        TerminalSession session,
        ShellType shellType,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Read initial output for a short period to detect shell integration markers
        var absoluteTimeout = TimeSpan.FromSeconds(10);
        var idleTimeout = TimeSpan.FromSeconds(3);
        var startTime = DateTimeOffset.UtcNow;
        var hasCommandReady = false;

        void HandleMarker(in ShellIntegrationMarker marker)
        {
            if (marker.Type == ShellIntegrationMarkerType.CommandReady)
            {
                hasCommandReady = true;
            }
        }

        logger.LogDebug(
            "[Detect] Starting Shell Integration detection for {ShellType} (Idle: {Idle}s, Max: {Max}s)",
            shellType,
            idleTimeout.TotalSeconds,
            absoluteTimeout.TotalSeconds);

        session.Parser.ShellIntegrationMarkerReceived += HandleMarker;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remainingAbsolute = absoluteTimeout - (DateTimeOffset.UtcNow - startTime);
                if (remainingAbsolute <= TimeSpan.Zero)
                {
                    logger.LogWarning("[Detect] Absolute timeout reached ({AbsoluteTimeout}s). Stream was too noisy.", absoluteTimeout.TotalSeconds);
                    break;
                }

                var readTimeout = remainingAbsolute < idleTimeout ? remainingAbsolute : idleTimeout;
                var outcome = await session.ReadOrIdleAsync(readTimeout, cancellationToken);
                if (outcome == TerminalReadOutcome.Idle)
                {
                    if (readTimeout == remainingAbsolute)
                    {
                        logger.LogWarning(
                            "[Detect] Absolute timeout reached ({AbsoluteTimeout}s). Stream was too noisy.",
                            absoluteTimeout.TotalSeconds);
                    }
                    else
                    {
                        logger.LogInformation(
                            "[Detect] Idle timeout reached ({IdleTimeout}s). CommandReady marker not found, output settled.",
                            idleTimeout.TotalSeconds);
                    }

                    break;
                }

                if (outcome == TerminalReadOutcome.EndOfStream)
                {
                    if (hasCommandReady)
                    {
                        logger.LogDebug(
                            "[Detect] Shell Integration command-ready detected for {ShellType} in {Seconds}s, using Rich strategy",
                            shellType,
                            (DateTimeOffset.UtcNow - startTime).TotalSeconds);

                        return new RichExecuteStrategy(logger);
                    }

                    logger.LogWarning("[Detect] PTY stream closed unexpectedly during detection.");
                    break;
                }

                if (hasCommandReady)
                {
                    logger.LogDebug(
                        "[Detect] Shell Integration command-ready detected for {ShellType} in {Seconds}s, using Rich strategy",
                        shellType,
                        (DateTimeOffset.UtcNow - startTime).TotalSeconds);

                    return new RichExecuteStrategy(logger);
                }
            }
        }
        finally
        {
            session.Parser.ShellIntegrationMarkerReceived -= HandleMarker;
        }

        logger.LogInformation(
            "[Detect] Falling back to None strategy for {ShellType} in {Seconds}s.",
            shellType,
            (DateTimeOffset.UtcNow - startTime).TotalSeconds);

        return new NoneExecuteStrategy(logger);
    }

    /// <summary>
    /// Start executing a command in the PTY and stream the discovered terminal runs.
    /// </summary>
    /// <param name="session">The terminal session to use.</param>
    /// <param name="script">The script to execute.</param>
    /// <param name="shellType"></param>
    /// <param name="timeout">Maximum time to wait for the command to complete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The terminal runs discovered while the command is executing.</returns>
    public async IAsyncEnumerable<TerminalRun> ExecuteAsync(
        TerminalSession session,
        string script,
        ShellType shellType,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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
        TimeSpan timeout,
        CancellationToken cancellationToken);

    protected interface IExecutionScope
    {
        Task ExecuteAsync();
    }
}
