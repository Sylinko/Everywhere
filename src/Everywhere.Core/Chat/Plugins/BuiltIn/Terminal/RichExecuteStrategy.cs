using System.Text;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace Everywhere.Chat.Plugins.BuiltIn.Terminal;

/// <summary>
/// Execute strategy used when Shell Integration (OSC 633) markers are detected.
/// Uses the markers to precisely extract command output between B (CommandReady) and A (PromptStart).
/// Falls back to idle detection if markers are incomplete (e.g., command crashes).
/// </summary>
internal sealed class RichExecuteStrategy(ILogger logger) : IExecuteStrategy
{
    public async Task<ExecuteResult> ExecuteAsync(
        IPtyConnection pty,
        string script,
        bool isMultiline,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var readBuffer = new byte[4096];
        var vtBuffer = new VirtualTerminalBuffer(1024);

        // Track Shell Integration markers
        int? exitCode = null;
        var commandExecuted = false;
        var commandFinished = false;
        var commandReadyLine = -1; // B marker line
        var promptStartLine = -1; // A marker line (after D)

        var parser = new VtSequenceParser(
            vtBuffer,
            (in marker) =>
            {
                switch (marker.Type)
                {
                    case ShellIntegrationMarkerType.CommandReady:
                        commandReadyLine = marker.Line;
                        logger.LogDebug("[Rich] B (CommandReady) at line {Line}", marker.Line);
                        break;
                    case ShellIntegrationMarkerType.CommandExecuted:
                        commandExecuted = true;
                        logger.LogDebug("[Rich] C (CommandExecuted) at line {Line}", marker.Line);
                        break;
                    case ShellIntegrationMarkerType.CommandFinished:
                        commandFinished = true;
                        exitCode = marker.ExitCode;
                        logger.LogDebug("[Rich] D (CommandFinished) exitCode={ExitCode} at line {Line}", marker.ExitCode, marker.Line);
                        break;
                    case ShellIntegrationMarkerType.PromptStart:
                        // A after D means the next prompt has started — command is done
                        if (commandExecuted)
                        {
                            promptStartLine = marker.Line;
                            logger.LogDebug("[Rich] A (PromptStart) after execution at line {Line}", marker.Line);
                        }
                        break;
                }
            });

        // Send the command
        await SendCommandAsync(pty, script, isMultiline, cancellationToken);

        // Read output until we get the D+A sequence or timeout.
        // Uses Task.WhenAny pattern for efficient async polling.
        const int pollIntervalMs = 150;
        var consecutiveIdlePolls = 0;
        const int minIdlePolls = 4; // 4 × 150ms = 600ms idle
        const int maxFallbackIdlePolls = 14; // ~2.1s fallback

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        Task<int>? pendingReadTask = null;

        try
        {
            while (!linkedToken.IsCancellationRequested)
            {
                // Check if we have the complete marker sequence
                if (commandFinished && promptStartLine >= 0)
                {
                    logger.LogDebug("[Rich] Complete marker sequence detected, extracting output");
                    break;
                }

                pendingReadTask ??= pty.ReaderStream.ReadAsync(readBuffer, linkedToken).AsTask();
                var pollDelayTask = Task.Delay(pollIntervalMs, linkedToken);
                var completedTask = await Task.WhenAny(pendingReadTask, pollDelayTask);

                if (completedTask == pendingReadTask)
                {
                    // Received new data
                    var bytesRead = await pendingReadTask;
                    pendingReadTask = null;

                    if (bytesRead == 0) break; // Stream closed

                    var text = Encoding.UTF8.GetString(readBuffer, 0, bytesRead);
                    parser.Feed(text);

                    if (text.Contains('\n') || text.Contains('\r'))
                    {
                        logger.LogDebug("[PTY RAW] {EscapeForLog}", OutputCleaner.EscapeForLog(text));
                    }

                    consecutiveIdlePolls = 0;
                }
                else
                {
                    // Idle — no data within pollIntervalMs
                    if (!commandExecuted) continue;

                    consecutiveIdlePolls++;
                    if (consecutiveIdlePolls >= minIdlePolls)
                    {
                        // Check if the last line looks like a shell prompt
                        var lastLine = vtBuffer.GetLastLine();
                        if (OutputCleaner.IsShellPrompt(lastLine))
                        {
                            logger.LogDebug("[Rich] Idle detected with prompt, last line: {LastLine}", lastLine);
                            break;
                        }

                        // Fallback: force exit if terminal has been silent for ~2s
                        if (consecutiveIdlePolls >= maxFallbackIdlePolls)
                        {
                            logger.LogWarning("[Rich] Maximum idle threshold reached without strict prompt match. Assuming complete.");
                            break;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            logger.LogWarning("[Rich] Read timed out");
            try { pty.Kill(); }
            catch { /* ignore */ }
        }

        // Extract output
        string output;
        if (commandReadyLine >= 0 && promptStartLine > commandReadyLine)
        {
            // Use precise B→A extraction
            output = vtBuffer.GetTextBetween(commandReadyLine + 1, promptStartLine - 1);
            logger.LogDebug(
                "[Rich] Extracted output between lines {Start} and {End}, length={Length}",
                commandReadyLine + 1,
                promptStartLine - 1,
                output.Length);
        }
        else
        {
            // Fallback: use full buffer text with heuristic cleaning
            output = vtBuffer.GetText();
            logger.LogDebug("[Rich] Fallback to full buffer text, length={Length}", output.Length);
        }

        // Defensive cleaning (strip any residual echo/prompt)
        output = OutputCleaner.CleanOutput(output, script);

        return new ExecuteResult(output, exitCode);
    }

    /// <summary>
    /// Send command using bracketed paste mode for multi-line commands.
    /// Requires Shell Integration + PSReadLine to correctly handle bracketed paste.
    /// </summary>
    public async Task SendCommandAsync(
        IPtyConnection pty,
        string script,
        bool isMultiline,
        CancellationToken cancellationToken)
    {
        if (isMultiline)
        {
            var normalizedScript = script.Replace("\r\n", "\r").Replace("\n", "\r");
            var scriptBytes = Encoding.UTF8.GetBytes(normalizedScript);

            // Bracketed paste: wrap entire script in \e[200~ ... \e[201~\r
            await pty.WriterStream.WriteAsync("\e[200~"u8.ToArray(), cancellationToken);
            await pty.WriterStream.WriteAsync(scriptBytes, cancellationToken);
            await pty.WriterStream.WriteAsync("\e[201~\r"u8.ToArray(), cancellationToken);
            await pty.WriterStream.FlushAsync(cancellationToken);
        }
        else
        {
            var trimmed = script.Trim();
            if (trimmed.Length > 0)
            {
                var lineBytes = Encoding.UTF8.GetBytes(trimmed);
                await pty.WriterStream.WriteAsync(lineBytes, cancellationToken);
                await pty.WriterStream.WriteAsync("\r"u8.ToArray(), cancellationToken);
                await pty.WriterStream.FlushAsync(cancellationToken);
            }
        }
    }
}