using System.Diagnostics;
using System.Threading.Channels;
using Everywhere.Common;
using Everywhere.Extensions;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace Everywhere.Terminal;

/// <summary>
/// Execute strategy used when Shell Integration (OSC 633) markers are detected.
/// Captures text in parser order between C (CommandExecuted) and D (CommandFinished)
/// markers so terminal redraws cannot reorder multi-line command output.
/// Falls back to idle detection if markers are incomplete (e.g., command crashes).
/// </summary>
public sealed partial class RichExecuteStrategy(ILogger logger) : ExecuteStrategy
{
    protected override IExecutionScope CreateExecutionScope(
        ChannelWriter<TerminalRun> writer,
        TerminalSession session,
        string script,
        ShellType shellType,
        TimeSpan? timeout,
        CancellationToken cancellationToken
    ) => new ExecutionScope(writer, session, script, shellType, timeout, logger, cancellationToken);

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
        private readonly TerminalLineBuffer fallbackBuffer = new();

        private TerminalRun? _activeRun;
        private string? _pendingCommandLine;
        private int _commandCount;
        private int _submittedCommandCount = 1;
        private bool _usedLineByLine;
        private bool _hasCommandFinished;
        private bool _hasFinalPrompt;
        private bool _hasFinalCommandReady;
        private bool _hasFinishedWithoutC;
        private int? _finishedWithoutCExitCode;
        private bool _hasLoggedActiveIdleWait;
        private bool _hasLoggedFinalPrompt;
        private bool _timedOut;
        private bool _reachedEndOfStream;
        private PtyTermination? _termination;
        private bool _isOutputIncomplete;
        private int _promptStartLine = -1; // Final A marker line (after all commands)
        private bool _capturingFallback;

        public async Task ExecuteAsync()
        {
            var shellKind = ShellIntegrationScript.GetShellKind(shellType, session.ShellPath);
            var isMultiline = OutputCleaner.IsMultilineCommand(script, shellType);
            var isBracketedPasteSupported = isMultiline && await session.IsBracketedPasteInputSupportedAsync(shellType, cancellationToken);
            _usedLineByLine = isMultiline && !isBracketedPasteSupported;

            session.Parser.ShellIntegrationMarkerReceived += HandleMarker;

            try
            {
                session.Parser.BeginCapture(fallbackBuffer);
                _capturingFallback = true;

                _submittedCommandCount = await SendCommandAsync(
                    session,
                    script,
                    shellKind,
                    shellType,
                    isMultiline,
                    isBracketedPasteSupported,
                    logger,
                    cancellationToken);

                const int pollIntervalMs = 150;
                var consecutiveIdlePolls = 0;
                const int minIdlePolls = 4; // 4 x 150ms = 600ms idle after D -> A (for mock/line-by-line streams without B)
                const int silentLogPolls = 14; // ~2.1s silent diagnostic log only; never terminates command

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
                        try
                        {
                            if (_commandCount > 0 && _hasCommandFinished && _hasFinalPrompt && !_hasLoggedFinalPrompt)
                            {
                                _hasLoggedFinalPrompt = true;
                                LogRichCommandOutputReachedPrompt(_commandCount, _promptStartLine);
                            }

                            if (HasReachedTerminalPrompt())
                            {
                                break;
                            }

                            var (outcome, termination) = await session.ReadOrWaitAsync(
                                TimeSpan.FromMilliseconds(pollIntervalMs),
                                rootExitTask,
                                linkedToken);

                            if (outcome == TerminalReadOutcome.Data)
                            {
                                consecutiveIdlePolls = 0;
                            }
                            else if (outcome == TerminalReadOutcome.EndOfStream)
                            {
                                _reachedEndOfStream = true;
                                break;
                            }
                            else if (outcome == TerminalReadOutcome.Terminated)
                            {
                                _termination = termination;
                                _isOutputIncomplete = !await session.DrainUntilEndOfStreamAsync(TimeSpan.FromSeconds(2), linkedToken);
                                if (_isOutputIncomplete)
                                {
                                    LogRichIncompleteOutputAfterTermination(shellKind);
                                }
                                break;
                            }
                            else
                            {
                                consecutiveIdlePolls++;
                                if (_activeRun is not null)
                                {
                                    // Never complete a running command on idle or prompt regex while C -> D is pending.
                                    if (!_hasLoggedActiveIdleWait && consecutiveIdlePolls >= silentLogPolls)
                                    {
                                        _hasLoggedActiveIdleWait = true;
                                        LogRichCommandIsSilentButStillRunning();
                                    }

                                    continue;
                                }

                                // Only after D has already settled the active run can A + short idle finish
                                // (supports line-by-line multi-command batches and test sequences omitting trailing B).
                                if (_hasCommandFinished && _hasFinalPrompt && consecutiveIdlePolls >= minIdlePolls)
                                {
                                    break;
                                }
                            }
                        }
                        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                        {
                            _timedOut = true;
                            RecordRichDegradation(shellKind, "read_timeout");
                            LogRichReadTimedOut(shellKind);
                            try
                            {
                                session.Pty.Kill();
                            }
                            catch
                            {
                                // ignore
                            }

                            break;
                        }
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

                if (timeoutCts.IsCancellationRequested && !_timedOut)
                {
                    _timedOut = true;
                    RecordRichDegradation(shellKind, "read_timeout");
                    LogRichReadTimedOut(shellKind);
                    try
                    {
                        session.Pty.Kill();
                    }
                    catch
                    {
                        // ignore
                    }
                }

                session.Parser.EndCapture();
                _capturingFallback = false;
                if (_activeRun is not null)
                {
                    await CompleteRunAsync(_activeRun);
                    _activeRun = null;
                }

                if (_commandCount == 0)
                {
                    var run = new TerminalRun(script);
                    writer.TryWrite(run);

                    // Fallback: use capture text since command send, then clean the command echo/prompt.
                    var rawOutput = fallbackBuffer.GetText();
                    var output = OutputCleaner.StripCommandEchoAndPrompt(rawOutput, script);
                    RecordRichDegradation(shellKind, "no_c_marker_fallback");
                    LogRichFallbackToCapturedText(shellKind, rawOutput.Length, output.Length);

                    run.Output.ReplaceText(output);
                    await CompleteRunAsync(run, fallbackExitCode: _hasFinishedWithoutC ? _finishedWithoutCExitCode : null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    session.Pty.Kill();
                }
                catch
                {
                    // ignore
                }

                _activeRun?.Cancel();
                throw;
            }
            catch (Exception ex)
            {
                _activeRun?.Fail(ex);
                throw;
            }
            finally
            {
                session.Parser.EndCapture();
                session.Parser.ShellIntegrationMarkerReceived -= HandleMarker;
            }
        }

        private bool HasReachedTerminalPrompt()
        {
            if (_commandCount == 0)
            {
                return _hasFinishedWithoutC && _hasFinalCommandReady;
            }

            if (!_hasCommandFinished || !_hasFinalCommandReady)
            {
                return false;
            }

            return !_usedLineByLine || _commandCount >= _submittedCommandCount;
        }

        private async ValueTask CompleteRunAsync(TerminalRun run, int? fallbackExitCode = null)
        {
            if (_timedOut)
            {
                run.Timeout();
                return;
            }

            if (_termination is { } observedTermination)
            {
                var exitCode = observedTermination.Kind == PtyTerminationKind.Exited
                    ? observedTermination.Value
                    : fallbackExitCode;
                run.Complete(exitCode, observedTermination, _isOutputIncomplete);
                return;
            }

            if (_reachedEndOfStream)
            {
                var termination = await session.TryObserveTerminationAsync(TimeSpan.FromSeconds(2), cancellationToken);
                var exitCode = termination?.Kind == PtyTerminationKind.Exited
                    ? termination.Value.Value
                    : fallbackExitCode;
                run.Complete(exitCode, termination);
                return;
            }

            run.Complete(fallbackExitCode);
        }

        private void HandleMarker(in ShellIntegrationMarker marker)
        {
            switch (marker.Type)
            {
                case ShellIntegrationMarkerType.CommandReady:
                    if (_hasCommandFinished && _commandCount > 0 || _hasFinishedWithoutC && _commandCount == 0)
                    {
                        _hasFinalCommandReady = true;
                    }
                    LogRichBCommandReady(marker.Line);
                    break;
                case ShellIntegrationMarkerType.CommandLine:
                    _pendingCommandLine = marker.CommandLine;
                    LogRichECommandLine(marker.CommandLine, marker.Line);
                    break;
                case ShellIntegrationMarkerType.CommandExecuted:
                    _commandCount++;
                    _hasCommandFinished = false;
                    _hasFinalPrompt = false;
                    _hasFinalCommandReady = false;
                    _hasLoggedActiveIdleWait = false;
                    _hasLoggedFinalPrompt = false;
                    if (_capturingFallback)
                    {
                        session.Parser.EndCapture();
                        _capturingFallback = false;
                    }

                    if (_activeRun is null)
                    {
                        _activeRun = CreateRun();
                    }
                    else
                    {
                        ApplyPendingCommandLine(_activeRun, append: true);
                    }

                    session.Parser.BeginCapture(_activeRun.Output);
                    _pendingCommandLine = null;
                    LogRichCCommandExecuted(_commandCount, marker.Line);
                    break;
                case ShellIntegrationMarkerType.CommandFinished:
                    if (_commandCount > 0 && _activeRun is not null)
                    {
                        session.Parser.EndCapture();
                        _hasCommandFinished = true;
                        _activeRun.Complete(marker.ExitCode);
                        _activeRun = null;
                    }
                    else if (_commandCount == 0)
                    {
                        _hasFinishedWithoutC = true;
                        _finishedWithoutCExitCode = marker.ExitCode;
                    }

                    LogRichDCommandFinished(marker.ExitCode, marker.Line);
                    break;
                case ShellIntegrationMarkerType.PromptStart:
                    // A after commands have started means a prompt has arrived.
                    if (_commandCount > 0 && _hasCommandFinished || _hasFinishedWithoutC && _commandCount == 0)
                    {
                        _hasFinalPrompt = true;
                        _promptStartLine = marker.Line;
                        LogRichAPromptStart(marker.Line);
                    }

                    break;
            }
        }

        private TerminalRun CreateRun()
        {
            var run = new TerminalRun(string.IsNullOrEmpty(_pendingCommandLine) ? script : _pendingCommandLine);
            writer.TryWrite(run);
            return run;
        }

        private void ApplyPendingCommandLine(TerminalRun run, bool append)
        {
            if (!string.IsNullOrEmpty(_pendingCommandLine))
            {
                run.SetCommandLine(_pendingCommandLine, append);
            }
        }

        /// <summary>
        /// Send command using bracketed paste mode for multi-line commands when the
        /// shell has proven that bracketed paste input is consumed atomically.
        /// </summary>
        private async static Task<int> SendCommandAsync(
            TerminalSession session,
            string script,
            string shellKind,
            ShellType shellType,
            bool isMultiline,
            bool useBracketedPaste,
            ILogger logger,
            CancellationToken cancellationToken
        )
        {
            if (isMultiline)
            {
                if (!useBracketedPaste)
                {
                    RecordRichDegradation(shellKind, "bracketed_paste_probe_failed");
                    LogRichAtomicBracketedPasteInputIsNotSupported(logger, shellKind, shellType);

                    return await SendLineByLineAsync(session, script, cancellationToken);
                }

                LogRichSendingMultiLineCommandUsingBracketedPaste(logger, shellKind, shellType, session.Dimensions);

                var normalizedScript = script.Replace("\r\n", "\n").Replace("\r", "\n");
                await session.WriteInputAsync($"\e[200~{normalizedScript}\e[201~\r", cancellationToken);
                return 1;
            }

            LogRichSendingSingleLineCommand(logger, shellKind, shellType);

            var trimmed = script.Trim();
            if (trimmed.Length > 0)
            {
                await session.WriteInputAsync($"{NormalizeCommandNewlines(trimmed)}\r", cancellationToken);
            }

            return 1;
        }

        private static void RecordRichDegradation(string shellKind, string reason)
        {
            Activity.Current?.SetTag("terminal.rich.degraded", true);
            Activity.Current?.SetTag("terminal.rich.degradation_reason", reason);
            TerminalTelemetry.RichDegradations.Add(
                1,
                new TagList
                {
                    { "shell.kind", shellKind },
                    { "reason", reason }
                });
        }

        private async static Task<int> SendLineByLineAsync(
            TerminalSession session,
            string script,
            CancellationToken cancellationToken
        )
        {
            var lines = script.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            var sentCount = 0;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                sentCount++;
                await session.WriteInputAsync($"{trimmed}\r", cancellationToken);
                await Task.Delay(100, cancellationToken);
            }

            return Math.Max(sentCount, 1);
        }

        private static string NormalizeCommandNewlines(string script)
        {
            return script.Replace("\r\n", "\r").Replace("\n", "\r");
        }

        [LoggerMessage(LogLevel.Debug, "[Rich] Command output reached prompt after {Count} C marker(s), final prompt at line {Line}")]
        partial void LogRichCommandOutputReachedPrompt(int count, int line);

        [LoggerMessage(LogLevel.Debug, "[Rich] Command is silent but still running; waiting for D marker.")]
        partial void LogRichCommandIsSilentButStillRunning();

        [LoggerMessage(
            LogLevel.Warning,
            "[Rich] PTY drain timed out after root process exited (shellKind={ShellKind}); output may be incomplete.")]
        partial void LogRichIncompleteOutputAfterTermination(string shellKind);

        [LoggerMessage(LogLevel.Warning, "[Rich] Read timed out (shellKind={ShellKind})")]
        partial void LogRichReadTimedOut(string shellKind);

        [LoggerMessage(
            LogLevel.Debug,
            "[Rich] Fallback to captured text (shellKind={ShellKind}), raw length={Length}, cleaned length={CleanedLength}")]
        partial void LogRichFallbackToCapturedText(string shellKind, int length, int cleanedLength);

        [LoggerMessage(LogLevel.Debug, "[Rich] B (CommandReady) at line {Line}")]
        partial void LogRichBCommandReady(int line);

        [LoggerMessage(LogLevel.Debug, "[Rich] E (CommandLine) command={CommandLine} at line {Line}")]
        partial void LogRichECommandLine(string? commandLine, int line);

        [LoggerMessage(LogLevel.Debug, "[Rich] C (CommandExecuted) # {Count} at line {Line}")]
        partial void LogRichCCommandExecuted(int count, int line);

        [LoggerMessage(LogLevel.Debug, "[Rich] D (CommandFinished) exitCode={ExitCode} at line {Line}")]
        partial void LogRichDCommandFinished(int? exitCode, int line);

        [LoggerMessage(LogLevel.Debug, "[Rich] A (PromptStart) at line {Line}")]
        partial void LogRichAPromptStart(int line);

        [LoggerMessage(
            LogLevel.Warning,
            "[Rich] Atomic bracketed paste input is not supported; sending multi-line command as Enter-separated lines (shellKind={ShellKind}, shellType={ShellType})")]
        static partial void LogRichAtomicBracketedPasteInputIsNotSupported(ILogger logger, string shellKind, ShellType shellType);

        [LoggerMessage(
            LogLevel.Debug,
            "[Rich] Sending multi-line command using bracketed paste (shellKind={ShellKind}, shellType={ShellType}, dimensions={Dimensions})")]
        static partial void LogRichSendingMultiLineCommandUsingBracketedPaste(
            ILogger logger,
            string shellKind,
            ShellType shellType,
            TerminalDimensions dimensions
        );

        [LoggerMessage(LogLevel.Debug, "[Rich] Sending single-line command (shellKind={ShellKind}, shellType={ShellType})")]
        static partial void LogRichSendingSingleLineCommand(ILogger logger, string shellKind, ShellType shellType);
    }
}
