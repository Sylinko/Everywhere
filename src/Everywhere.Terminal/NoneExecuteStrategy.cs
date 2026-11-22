using System.Threading.Channels;
using Everywhere.Common;
using Everywhere.Extensions;
using Microsoft.Extensions.Logging;
using Porta.Pty;
using ZLinq;

namespace Everywhere.Terminal;

/// <summary>
/// Execute strategy used when Shell Integration is NOT available.
/// Falls back to idle detection + prompt heuristics.
/// Uses Task.WhenAny pattern instead of CancellationToken-based polling to avoid
/// deadlocks when an earlier phase has a pending read.
/// </summary>
public sealed partial class NoneExecuteStrategy(ILogger logger) : ExecuteStrategy
{
    protected override IExecutionScope CreateExecutionScope(
        ChannelWriter<TerminalRun> writer,
        TerminalSession session,
        string script,
        ShellType shellType,
        TimeSpan? timeout,
        CancellationToken cancellationToken) =>
        new ExecutionScope(writer, session, script, shellType, timeout, logger, cancellationToken);

    private sealed partial class ExecutionScope(
        ChannelWriter<TerminalRun> writer,
        TerminalSession session,
        string script,
        ShellType shellType,
        TimeSpan? timeout,
        ILogger logger,
        CancellationToken cancellationToken
    ) : IExecutionScope
    {
        public async Task ExecuteAsync()
        {
            var shellKind = ShellIntegrationScript.GetShellKind(shellType, session.ShellPath);
            var run = new TerminalRun(script);
            writer.TryWrite(run);

            try
            {
                var (timedOut, reachedEndOfStream, termination, isOutputIncomplete) = await ExecuteWithCaptureAsync(run, shellKind);
                LogNoneOutputLength(shellKind, run.OutputText.Length);

                if (timedOut)
                {
                    run.Timeout();
                }
                else if (termination is { } observedTermination)
                {
                    var exitCode = observedTermination.Kind == PtyTerminationKind.Exited
                        ? observedTermination.Value
                        : (int?)null;
                    run.Complete(exitCode, observedTermination, isOutputIncomplete);
                }
                else if (reachedEndOfStream)
                {
                    var eofTermination = await session.TryObserveTerminationAsync(TimeSpan.FromSeconds(2), cancellationToken);
                    var exitCode = eofTermination?.Kind == PtyTerminationKind.Exited
                        ? eofTermination.Value.Value
                        : (int?)null;
                    run.Complete(exitCode, eofTermination);
                }
                else
                {
                    run.Complete(null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try { session.Pty.Kill(); }
                catch
                {
                    // ignore
                }
                run.Cancel();
                throw;
            }
            catch (Exception ex)
            {
                run.Fail(ex);
                throw;
            }
        }

        private async ValueTask<(bool TimedOut, bool ReachedEndOfStream, PtyTermination? Termination, bool IsOutputIncomplete)>
            ExecuteWithCaptureAsync(TerminalRun run, string shellKind)
        {
            try
            {
                // Wait for terminal to become idle (quiet 200ms = idle, max wait 5s)
                LogNoneWaitingForInitialIdle(shellKind);
                await session.WaitForIdleAsync(
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(200),
                    cancellationToken);

                LogNoneSendingCtrlC();
                await session.WriteInputAsync("\x03"u8, cancellationToken);

                // Brief wait for Ctrl+C echo to settle (quiet 200ms = idle, max wait 500ms)
                await session.WaitForIdleAsync(
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromMilliseconds(200),
                    cancellationToken);

                LogNoneSendingCommand(shellKind, shellType);
                session.Parser.BeginCapture(run.Output);
                await SendCommandAsync();

                LogNoneWaitingForCommandOutputStart();
                await WaitForCommandOutputStartAsync(run.Output);

                LogNoneWaitingForIdleWithPromptHeuristics();
                var hasReceivedData = HasCapturedData(run.Output);
                var consecutiveIdlePolls = 0;
                const int pollIntervalMs = 150;
                const int minIdlePolls = 4; // 4 x 150ms = 600ms idle
                const int maxFallbackIdlePolls = 200; // ~30s safety cap if an unrecognized prompt never matches
                var timedOut = false;
                var reachedEndOfStream = false;
                PtyTermination? observedTermination = null;
                var isOutputIncomplete = false;

                var effectiveTimeout = timeout ?? Timeout.InfiniteTimeSpan;
                using var timeoutCts = effectiveTimeout == Timeout.InfiniteTimeSpan
                    ? new CancellationTokenSource()
                    : new CancellationTokenSource(effectiveTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                var linkedToken = linkedCts.Token;
                using var terminationObserveCts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
                var rootExitTask = session.Pty.WaitForTerminationAsync(terminationObserveCts.Token);

                try
                {
                    while (!linkedToken.IsCancellationRequested)
                    {
                        var (outcome, termination) = await session.ReadOrWaitAsync(
                            TimeSpan.FromMilliseconds(pollIntervalMs),
                            rootExitTask,
                            linkedToken);
                        if (outcome == TerminalReadOutcome.Data)
                        {
                            consecutiveIdlePolls = 0;
                            hasReceivedData = true;

                            if (CheckForPrompt(run.Output))
                            {
                                LogNonePromptObservedAfterData();
                            }
                        }
                        else if (outcome == TerminalReadOutcome.EndOfStream)
                        {
                            reachedEndOfStream = true;
                            break;
                        }
                        else if (outcome == TerminalReadOutcome.Terminated)
                        {
                            observedTermination = termination;
                            isOutputIncomplete = !await session.DrainUntilEndOfStreamAsync(TimeSpan.FromSeconds(2), linkedToken);
                            if (isOutputIncomplete)
                            {
                                LogNoneIncompleteOutputAfterTermination(shellKind);
                            }
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
                                    LogNoneIdleDetectedWithPrompt();
                                    break;
                                }

                                // Fallback: if terminal stays silent for ~30s without prompt match,
                                // settle to prevent indefinite hangs on unrecognized prompts.
                                if (consecutiveIdlePolls >= maxFallbackIdlePolls)
                                {
                                    LogNoneMaximumIdleThresholdReached(shellKind, shellType, run.Output.Count, session.BytesRead);
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    timedOut = true;
                    try
                    {
                        session.Pty.Kill();
                    }
                    catch
                    {
                        // ignore
                    }
                }
                finally
                {
                    await terminationObserveCts.CancelAsync();
                    rootExitTask.Detach(IExceptionHandler.DangerouslyIgnoreAllException);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        session.Pty.Kill();
                    }
                    catch
                    {
                        // ignore
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (timeoutCts.IsCancellationRequested && !timedOut)
                {
                    timedOut = true;
                    try
                    {
                        session.Pty.Kill();
                    }
                    catch
                    {
                        // ignore
                    }
                }

                if (timedOut)
                {
                    LogNoneTimeoutReached(shellKind, shellType, run.Output.Count, session.BytesRead);
                }

                return (timedOut, reachedEndOfStream, observedTermination, isOutputIncomplete);
            }
            finally
            {
                session.Parser.EndCapture();
            }
        }

        /// <summary>
        /// Check if the active cursor line looks like a shell prompt.
        /// Inspecting only the cursor line avoids treating completed stdout lines
        /// (which moved the cursor onto a blank line below) as prompts.
        /// </summary>
        private static bool CheckForPrompt(TerminalLineBuffer output)
        {
            var cursorLine = output.GetCursorLineText();
            return !string.IsNullOrWhiteSpace(cursorLine) && OutputCleaner.IsShellPrompt(cursorLine);
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

        [LoggerMessage(LogLevel.Debug, "[None] Waiting for initial idle (shellKind={ShellKind})")]
        partial void LogNoneWaitingForInitialIdle(string shellKind);

        [LoggerMessage(LogLevel.Debug, "[None] Sending Ctrl+C to cancel residual input")]
        partial void LogNoneSendingCtrlC();

        [LoggerMessage(LogLevel.Debug, "[None] Sending command (shellKind={ShellKind}, shellType={ShellType})")]
        partial void LogNoneSendingCommand(string shellKind, ShellType shellType);

        [LoggerMessage(LogLevel.Debug, "[None] Waiting for command output to start")]
        partial void LogNoneWaitingForCommandOutputStart();

        [LoggerMessage(LogLevel.Debug, "[None] Waiting for idle with prompt heuristics")]
        partial void LogNoneWaitingForIdleWithPromptHeuristics();

        [LoggerMessage(LogLevel.Debug, "[None] Prompt observed after receiving data")]
        partial void LogNonePromptObservedAfterData();

        [LoggerMessage(LogLevel.Debug, "[None] Idle detected with prompt")]
        partial void LogNoneIdleDetectedWithPrompt();

        [LoggerMessage(
            LogLevel.Warning,
            "[None] PTY drain timed out after root process exited (shellKind={ShellKind}); output may be incomplete.")]
        partial void LogNoneIncompleteOutputAfterTermination(string shellKind);

        [LoggerMessage(
            LogLevel.Warning,
            "[None] Maximum idle threshold reached without strict prompt match (shellKind={ShellKind}, shellType={ShellType}, lineCount={LineCount}, bytesRead={BytesRead}). Assuming complete.")]
        partial void LogNoneMaximumIdleThresholdReached(string shellKind, ShellType shellType, int lineCount, long bytesRead);

        [LoggerMessage(
            LogLevel.Warning,
            "[None] Timeout reached (shellKind={ShellKind}, shellType={ShellType}, lineCount={LineCount}, bytesRead={BytesRead})")]
        partial void LogNoneTimeoutReached(string shellKind, ShellType shellType, int lineCount, long bytesRead);

        [LoggerMessage(LogLevel.Debug, "[None] Output length={Length} (shellKind={ShellKind})")]
        partial void LogNoneOutputLength(string shellKind, int length);
    }
}