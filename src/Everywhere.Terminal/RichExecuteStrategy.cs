using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Everywhere.Terminal;

/// <summary>
/// Execute strategy used when Shell Integration (OSC 633) markers are detected.
/// Captures text in parser order between C (CommandExecuted) and D (CommandFinished)
/// markers so terminal redraws cannot reorder multi-line command output.
/// Falls back to idle detection if markers are incomplete (e.g., command crashes).
/// </summary>
public sealed class RichExecuteStrategy(ILogger logger) : ExecuteStrategy
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
        private readonly TerminalLineBuffer fallbackBuffer = new();

        private TerminalRun? _activeRun;
        private string? _pendingCommandLine;
        private int _commandCount;
        private bool _hasReceivedData;
        private bool _hasCommandFinished;
        private bool _hasFinalPrompt;
        private bool _hasLoggedActiveIdleWait;
        private bool _hasLoggedFinalPrompt;
        private bool _timedOut;
        private int _promptStartLine = -1; // Final A marker line (after all commands)
        private bool _capturingFallback;

        public async Task ExecuteAsync()
        {
            var isMultiline = OutputCleaner.IsMultilineCommand(script, shellType);
            var isBracketedPasteSupported = await session.IsBracketedPasteInputSupportedAsync(shellType, cancellationToken);

            session.Parser.ShellIntegrationMarkerReceived += HandleMarker;

            try
            {
                session.Parser.BeginCapture(fallbackBuffer);
                _capturingFallback = true;

                await SendCommandAsync(
                    session,
                    script,
                    shellType,
                    isMultiline,
                    isBracketedPasteSupported,
                    logger,
                    cancellationToken);

                const int pollIntervalMs = 150;
                var consecutiveIdlePolls = 0;
                const int minIdlePolls = 4; // 4 x 150ms = 600ms idle
                const int maxFallbackIdlePolls = 14; // ~2.1s fallback

                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                var linkedToken = linkedCts.Token;

                while (!linkedToken.IsCancellationRequested)
                {
                    try
                    {
                        // D/A means a prompt has arrived, but there may still be queued output
                        // from commands already submitted line-by-line. Let EOF/idle settle it.
                        if (_commandCount > 0 && _hasCommandFinished && _hasFinalPrompt && !_hasLoggedFinalPrompt)
                        {
                            _hasLoggedFinalPrompt = true;
                            logger.LogDebug(
                                "[Rich] Command output reached prompt after {Count} C marker(s), final prompt at line {Line}",
                                _commandCount,
                                _promptStartLine);
                        }

                        var outcome = await session.ReadOrIdleAsync(TimeSpan.FromMilliseconds(pollIntervalMs), linkedToken);
                        if (outcome == TerminalReadOutcome.Data)
                        {
                            consecutiveIdlePolls = 0;
                            _hasReceivedData = true;
                        }
                        else if (outcome == TerminalReadOutcome.EndOfStream)
                        {
                            break;
                        }
                        else
                        {
                            if (_commandCount == 0)
                            {
                                if (!_hasReceivedData) continue;

                                consecutiveIdlePolls++;
                                var fallbackLastLine = fallbackBuffer.GetLastNonEmptyLine();
                                if (OutputCleaner.IsShellPrompt(fallbackLastLine))
                                {
                                    logger.LogDebug("[Rich] Idle detected with prompt before C marker, last line: {LastLine}", fallbackLastLine);
                                    break;
                                }

                                if (consecutiveIdlePolls >= maxFallbackIdlePolls)
                                {
                                    logger.LogWarning("[Rich] Maximum idle threshold reached before C marker. Falling back to screen text.");
                                    break;
                                }

                                continue;
                            }

                            consecutiveIdlePolls++;
                            if (consecutiveIdlePolls < minIdlePolls) continue;

                            var lastLine = _activeRun?.Output.GetLastNonEmptyLine() ?? string.Empty;
                            if (_hasFinalPrompt || OutputCleaner.IsShellPrompt(lastLine))
                            {
                                logger.LogDebug("[Rich] Idle detected with prompt, last line: {LastLine}", lastLine);
                                break;
                            }

                            if (_activeRun is not null)
                            {
                                if (!_hasLoggedActiveIdleWait && consecutiveIdlePolls >= maxFallbackIdlePolls)
                                {
                                    _hasLoggedActiveIdleWait = true;
                                    logger.LogDebug("[Rich] Command is silent but still running; waiting for D marker.");
                                }

                                continue;
                            }

                            if (consecutiveIdlePolls >= maxFallbackIdlePolls)
                            {
                                logger.LogWarning("[Rich] Maximum idle threshold reached after run completed without prompt. Assuming settled.");
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                    {
                        _timedOut = true;
                        logger.LogWarning("[Rich] Read timed out");
                        try { session.Pty.Kill(); }
                        catch
                        {
                            // ignore
                        }
                        break;
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (timeoutCts.IsCancellationRequested && !_timedOut)
                {
                    _timedOut = true;
                    logger.LogWarning("[Rich] Read timed out");
                    try { session.Pty.Kill(); }
                    catch
                    {
                        // ignore
                    }
                }

                session.Parser.EndCapture();
                _capturingFallback = false;
                if (_activeRun is not null)
                {
                    if (_timedOut) _activeRun.Timeout();
                    else _activeRun.Complete(null);

                    _activeRun = null;
                }

                if (_commandCount == 0)
                {
                    var run = new TerminalRun(script);
                    writer.TryWrite(run);

                    // Fallback: use capture text since command send, then clean the command echo/prompt.
                    var rawOutput = fallbackBuffer.GetText();
                    var output = OutputCleaner.StripCommandEchoAndPrompt(rawOutput, script);
                    logger.LogDebug(
                        "[Rich] Fallback to captured text, raw length={Length}, cleaned length={CleanedLength}",
                        rawOutput.Length,
                        output.Length);

                    run.Output.ReplaceText(output);
                    if (_timedOut) run.Timeout();
                    else run.Complete(null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
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

        private void HandleMarker(in ShellIntegrationMarker marker)
        {
            switch (marker.Type)
            {
                case ShellIntegrationMarkerType.CommandReady:
                    logger.LogDebug("[Rich] B (CommandReady) at line {Line}", marker.Line);
                    break;
                case ShellIntegrationMarkerType.CommandLine:
                    _pendingCommandLine = marker.CommandLine;
                    logger.LogDebug("[Rich] E (CommandLine) command={CommandLine} at line {Line}", marker.CommandLine, marker.Line);
                    break;
                case ShellIntegrationMarkerType.CommandExecuted:
                    _commandCount++;
                    _hasCommandFinished = false;
                    _hasFinalPrompt = false;
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
                    logger.LogDebug("[Rich] C (CommandExecuted) # {Count} at line {Line}", _commandCount, marker.Line);
                    break;
                case ShellIntegrationMarkerType.CommandFinished:
                    if (_commandCount > 0 && _activeRun is not null)
                    {
                        session.Parser.EndCapture();
                        _hasCommandFinished = true;
                        _activeRun.Complete(marker.ExitCode);
                        _activeRun = null;
                    }

                    logger.LogDebug(
                        "[Rich] D (CommandFinished) exitCode={ExitCode} at line {Line}",
                        marker.ExitCode,
                        marker.Line);
                    break;
                case ShellIntegrationMarkerType.PromptStart:
                    // A after commands have started means a prompt has arrived.
                    if (_commandCount > 0 && _hasCommandFinished)
                    {
                        _hasFinalPrompt = true;
                        _promptStartLine = marker.Line;
                        logger.LogDebug("[Rich] A (PromptStart) at line {Line}", marker.Line);
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
        private async static Task SendCommandAsync(
            TerminalSession session,
            string script,
            ShellType shellType,
            bool isMultiline,
            bool useBracketedPaste,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (isMultiline)
            {
                if (!useBracketedPaste)
                {
                    logger.LogWarning(
                        "[Rich] Atomic bracketed paste input is not supported; sending multi-line command as Enter-separated lines (shellType={ShellType})",
                        shellType);

                    await SendLineByLineAsync(session, script, cancellationToken);
                    return;
                }

                logger.LogDebug(
                    "[Rich] Sending multi-line command using bracketed paste (shellType={ShellType}, dimensions={Dimensions})",
                    shellType,
                    session.Dimensions);

                var normalizedScript = script.Replace("\r\n", "\n").Replace("\r", "\n");
                await session.WriteInputAsync($"\e[200~{normalizedScript}\e[201~\r", cancellationToken);
            }
            else
            {
                logger.LogDebug("[Rich] Sending single-line command (shellType={ShellType})", shellType);

                var trimmed = script.Trim();
                if (trimmed.Length > 0)
                {
                    await session.WriteInputAsync($"{NormalizeCommandNewlines(trimmed)}\r", cancellationToken);
                }
            }
        }

        private async static Task SendLineByLineAsync(
            TerminalSession session,
            string script,
            CancellationToken cancellationToken)
        {
            var lines = script.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                await session.WriteInputAsync($"{trimmed}\r", cancellationToken);
                await Task.Delay(100, cancellationToken);
            }
        }

        private static string NormalizeCommandNewlines(string script)
        {
            return script.Replace("\r\n", "\r").Replace("\n", "\r");
        }
    }
}
