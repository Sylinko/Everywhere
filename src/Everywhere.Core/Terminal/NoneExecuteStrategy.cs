using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Terminal;

/// <summary>
/// Execute strategy used when Shell Integration is NOT available.
/// Falls back to idle detection + prompt heuristics.
/// Uses Task.WhenAny pattern instead of CancellationToken-based polling to avoid
/// deadlocks when an earlier phase has a pending read.
/// </summary>
public sealed class NoneExecuteStrategy(ILogger logger) : ExecuteStrategy
{
    protected override IExecutionScope CreateExecutionScope(
        ChannelWriter<TerminalRun> writer,
        TerminalSession session,
        string script,
        ShellType shellType,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        new ExecutionScope(writer, session, script, shellType, timeout, logger, cancellationToken);

    private sealed class ExecutionScope(
        ChannelWriter<TerminalRun> writer,
        TerminalSession session,
        string script,
        ShellType shellType,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken
    ) : IExecutionScope
    {
        public async Task ExecuteAsync()
        {
            var run = new TerminalRun(script);
            writer.TryWrite(run);

            try
            {
                var timedOut = await ExecuteWithCaptureAsync(run);
                logger.LogDebug("[None] Captured output: {EscapeForLog}", OutputCleaner.EscapeForLog(run.OutputText));
                logger.LogDebug("[None] Output length={Length}", run.OutputText.Length);

                if (timedOut) run.Timeout();
                else run.Complete(null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                run.Cancel();
                throw;
            }
            catch (Exception ex)
            {
                run.Fail(ex);
                throw;
            }
        }

        private async ValueTask<bool> ExecuteWithCaptureAsync(TerminalRun run)
        {
            try
            {
                // Wait for terminal to become idle (quiet 200ms = idle, max wait 5s)
                logger.LogDebug("[None] Waiting for initial idle...");
                await session.WaitForIdleAsync(
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(200),
                    cancellationToken);

                logger.LogDebug("[None] Sending Ctrl+C to cancel residual input");
                await session.WriteInputAsync("\x03", cancellationToken);

                // Brief wait for Ctrl+C echo to settle (quiet 200ms = idle, max wait 500ms)
                await session.WaitForIdleAsync(
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromMilliseconds(200),
                    cancellationToken);

                logger.LogDebug("[None] Sending command (shellType={ShellType})", shellType);
                session.Parser.BeginCapture(run.Output);
                await SendCommandAsync();

                logger.LogDebug("[None] Waiting for command output to start");
                await WaitForCommandOutputStartAsync(run.Output);

                logger.LogDebug("[None] Waiting for idle with prompt heuristics");
                var hasReceivedData = HasCapturedData(run.Output);
                var consecutiveIdlePolls = 0;
                const int pollIntervalMs = 150;
                const int minIdlePolls = 4; // 4 x 150ms = 600ms idle
                const int maxFallbackIdlePolls = 14; // ~2.1s - force exit if prompt regex never matches
                var timedOut = false;

                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                var linkedToken = linkedCts.Token;

                try
                {
                    while (!linkedToken.IsCancellationRequested)
                    {
                        var outcome = await session.ReadOrIdleAsync(TimeSpan.FromMilliseconds(pollIntervalMs), linkedToken);
                        if (outcome == TerminalReadOutcome.Data)
                        {
                            consecutiveIdlePolls = 0;
                            hasReceivedData = true;

                            if (CheckForPrompt(run.Output))
                            {
                                logger.LogDebug("[None] Prompt observed after receiving data");
                            }
                        }
                        else if (outcome == TerminalReadOutcome.EndOfStream)
                        {
                            break;
                        }
                        else
                        {
                            // Idle - no data within pollIntervalMs
                            if (!hasReceivedData) continue;

                            consecutiveIdlePolls++;
                            if (consecutiveIdlePolls >= minIdlePolls)
                            {
                                if (CheckForPrompt(run.Output))
                                {
                                    logger.LogDebug("[None] Idle detected with prompt");
                                    break;
                                }

                                // Fallback: if terminal has been silent for ~2s without strict prompt match,
                                // assume command is complete to prevent hanging.
                                if (consecutiveIdlePolls >= maxFallbackIdlePolls)
                                {
                                    logger.LogWarning("[None] Maximum idle threshold reached without strict prompt match. Assuming complete.");
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    timedOut = true;
                    logger.LogWarning("[None] timeout reached");
                    try
                    {
                        session.Pty.Kill();
                    }
                    catch
                    {
                        // ignore
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (timeoutCts.IsCancellationRequested && !timedOut)
                {
                    timedOut = true;
                    logger.LogWarning("[None] timeout reached");
                    try
                    {
                        session.Pty.Kill();
                    }
                    catch
                    {
                        // ignore
                    }
                }

                return timedOut;
            }
            finally
            {
                session.Parser.EndCapture();
            }
        }

        /// <summary>
        /// Check if the newest captured run lines look like a shell prompt.
        /// This deliberately ignores the shared session screen so startup prompts
        /// cannot complete a fallback command early.
        /// </summary>
        private static bool CheckForPrompt(TerminalLineBuffer output)
        {
            for (var i = output.Count - 1; i >= 0 && i >= output.Count - 4; i--)
            {
                var line = output[i].Text;
                if (!string.IsNullOrWhiteSpace(line) && OutputCleaner.IsShellPrompt(line))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasCapturedData(TerminalLineBuffer output)
        {
            return output.AsValueEnumerable().Any(t => t.Text.Length > 0);
        }

        /// <summary>
        /// Send command line by line for multi-line commands.
        /// Does not use bracketed paste mode - each line is sent as a separate Enter keypress.
        /// This is safe without Shell Integration or PSReadLine.
        /// </summary>
        private async Task SendCommandAsync()
        {
            var isMultiline = OutputCleaner.IsMultilineCommand(script, shellType);
            if (isMultiline)
            {
                // Multi-line: split and send line by line
                var lines = script.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;

                    await session.WriteInputAsync($"{trimmed}\r", cancellationToken);
                    // Brief pause between lines to let the shell process each one
                    await Task.Delay(100, cancellationToken);
                }
            }
            else
            {
                var trimmed = script.Trim();
                if (trimmed.Length > 0)
                {
                    await session.WriteInputAsync($"{NormalizeCommandNewlines(trimmed)}\r", cancellationToken);
                }
            }
        }

        private static string NormalizeCommandNewlines(string script)
        {
            return script.Replace("\r\n", "\r").Replace("\n", "\r");
        }

        /// <summary>
        /// Wait for command output to begin.
        /// </summary>
        private async Task WaitForCommandOutputStartAsync(TerminalLineBuffer output)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var linkedToken = linkedCts.Token;

            try
            {
                while (!linkedToken.IsCancellationRequested)
                {
                    if (HasCapturedData(output)) return;

                    var outcome = await session.ReadOrIdleAsync(TimeSpan.FromMilliseconds(100), linkedToken);
                    if (outcome == TerminalReadOutcome.EndOfStream)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout while waiting for command output to start.
            }
        }
    }
}