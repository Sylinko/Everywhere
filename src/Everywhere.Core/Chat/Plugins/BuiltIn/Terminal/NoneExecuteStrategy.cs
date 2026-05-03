using System.Text;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace Everywhere.Chat.Plugins.BuiltIn.Terminal;

/// <summary>
/// Execute strategy used when Shell Integration is NOT available.
/// Falls back to idle detection + prompt heuristics + command echo stripping.
/// Uses Task.WhenAny pattern instead of CancellationToken-based polling to avoid
/// deadlocks when Phase 5 drains the read buffer before Phase 6 starts.
/// </summary>
internal sealed class NoneExecuteStrategy(ILogger logger) : IExecuteStrategy
{
    public async Task<ExecuteResult> ExecuteAsync(
        IPtyConnection pty,
        string script,
        bool isMultiline,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var rawOutput = await ExecuteWithRawOutputAsync(pty, script, isMultiline, timeout, cancellationToken);
        var output = OutputCleaner.CleanOutput(rawOutput, script);

        logger.LogDebug("[None] Output length={Length}", output.Length);
        return new ExecuteResult(output, ExitCode: null);
    }

    private async ValueTask<string> ExecuteWithRawOutputAsync(
        IPtyConnection pty,
        string script,
        bool isMultiline,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var readBuffer = new byte[4096];
        var vtBuffer = new VirtualTerminalBuffer(1024);
        var parser = new VtSequenceParser(vtBuffer);

        // Shared read task holder — carries the pending ReadAsync across all phases
        // so we never lose data that arrives between phases.
        var readTaskHolder = new ReadTaskHolder();

        // Wait for terminal to become idle (quiet 200ms = idle, max wait 5s)
        logger.LogDebug("[None] Waiting for initial idle...");
        await WaitForIdleAsync(
            pty,
            readBuffer,
            parser,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(200),
            readTaskHolder,
            cancellationToken);

        logger.LogDebug("[None] Sending Ctrl+C to cancel residual input");
        await pty.WriterStream.WriteAsync("\x03"u8.ToArray(), cancellationToken);
        await pty.WriterStream.FlushAsync(cancellationToken);

        // Brief wait for Ctrl+C echo to settle (quiet 100ms = idle, max wait 500ms)
        await WaitForIdleAsync(
            pty,
            readBuffer,
            parser,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(200),
            readTaskHolder,
            cancellationToken);

        // Record the start position (cursor line before command)
        var startLine = vtBuffer.CursorY;

        logger.LogDebug("[None] Sending command (multiline={IsMultiline})", isMultiline);
        await SendCommandAsync(pty, script, isMultiline, cancellationToken);

        logger.LogDebug("[None] Waiting for cursor to move past start line {StartLine}", startLine);
        await WaitForCursorMoveAsync(pty, readBuffer, parser, startLine, TimeSpan.FromSeconds(3), readTaskHolder, cancellationToken);

        logger.LogDebug("[None] Waiting for idle with prompt heuristics");
        var hasReceivedData = true;
        var consecutiveIdlePolls = 0;
        const int pollIntervalMs = 150;
        const int minIdlePolls = 4; // 4 × 150ms = 600ms idle
        const int maxFallbackIdlePolls = 14; // ~2.1s — force exit if prompt regex never matches

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            // Early check: if already drained the prompt into vtBuffer
            if (CheckForPrompt(vtBuffer))
            {
                logger.LogDebug("[None] Prompt detected immediately after command send");
                return vtBuffer.GetText();
            }

            while (!linkedToken.IsCancellationRequested)
            {
                readTaskHolder.Task ??= pty.ReaderStream.ReadAsync(readBuffer, linkedToken).AsTask();
                var completedTask = await Task.WhenAny(
                    readTaskHolder.Task,
                    Task.Delay(pollIntervalMs, linkedToken));

                if (completedTask == readTaskHolder.Task)
                {
                    // Received new data
                    var bytesRead = await readTaskHolder.Task;
                    readTaskHolder.Task = null;
                    if (bytesRead == 0) break;

                    var text = Encoding.UTF8.GetString(readBuffer, 0, bytesRead);
                    parser.Feed(text);

                    if (text.Contains('\n') || text.Contains('\r'))
                    {
                        logger.LogDebug("[PTY RAW] {EscapeForLog}", OutputCleaner.EscapeForLog(text));
                    }

                    consecutiveIdlePolls = 0;
                    hasReceivedData = true;

                    if (CheckForPrompt(vtBuffer))
                    {
                        logger.LogDebug("[None] Prompt detected after receiving data");
                        break;
                    }
                }
                else
                {
                    // Idle — no data within pollIntervalMs
                    if (!hasReceivedData) continue;

                    consecutiveIdlePolls++;
                    if (consecutiveIdlePolls >= minIdlePolls)
                    {
                        if (CheckForPrompt(vtBuffer))
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
            logger.LogWarning("[None] timeout reached");
            try
            {
                pty.Kill();
            }
            catch
            {
                // ignore
            }
        }

        return vtBuffer.GetText();
    }

    /// <summary>
    /// Check if the current cursor line (or the line above) looks like a shell prompt.
    /// Also checks the previous line because PSReadLine may shift the cursor.
    /// </summary>
    private static bool CheckForPrompt(VirtualTerminalBuffer vtBuffer)
    {
        var cursorLine = vtBuffer.GetCursorLine();
        if (OutputCleaner.IsShellPrompt(cursorLine)) return true;

        var prevLine = vtBuffer.GetLineText(Math.Max(0, vtBuffer.CursorY - 1));
        return !string.IsNullOrWhiteSpace(prevLine) && OutputCleaner.IsShellPrompt(prevLine);
    }

    /// <summary>
    /// Send command line by line for multi-line commands.
    /// Does not use bracketed paste mode — each line is sent as a separate Enter keypress.
    /// This is safe without Shell Integration or PSReadLine.
    /// </summary>
    public async Task SendCommandAsync(
        IPtyConnection pty,
        string script,
        bool isMultiline,
        CancellationToken cancellationToken)
    {
        if (!isMultiline)
        {
            var trimmed = script.Trim();
            if (trimmed.Length > 0)
            {
                await pty.WriterStream.WriteAsync(Encoding.UTF8.GetBytes(trimmed), cancellationToken);
                await pty.WriterStream.WriteAsync("\r"u8.ToArray(), cancellationToken);
                await pty.WriterStream.FlushAsync(cancellationToken);
            }

            return;
        }

        // Multi-line: split and send line by line
        var lines = script.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            await pty.WriterStream.WriteAsync(Encoding.UTF8.GetBytes(trimmed), cancellationToken);
            await pty.WriterStream.WriteAsync("\r"u8.ToArray(), cancellationToken);
            await pty.WriterStream.FlushAsync(cancellationToken);
            // Brief pause between lines to let the shell process each one
            await Task.Delay(100, cancellationToken);
        }
    }

    /// <summary>
    /// Wait for the PTY to become idle (no data for a short period).
    /// Separates max wait timeout from quiet period: if no data arrives within
    /// <paramref name="quietPeriod"/>, the terminal is considered idle.
    /// Uses Task.WhenAny to avoid CancellationToken-based exception overhead.
    /// </summary>
    private static async Task WaitForIdleAsync(
        IPtyConnection pty,
        byte[] readBuffer,
        VtSequenceParser parser,
        TimeSpan maxWait,
        TimeSpan quietPeriod,
        ReadTaskHolder readTaskHolder,
        CancellationToken cancellationToken)
    {
        var quietMs = (int)quietPeriod.TotalMilliseconds;
        using var timeoutCts = new CancellationTokenSource(maxWait);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            while (!linkedToken.IsCancellationRequested)
            {
                readTaskHolder.Task ??= pty.ReaderStream.ReadAsync(readBuffer, linkedToken).AsTask();
                var quietDelayTask = Task.Delay(quietMs, linkedToken);

                var completedTask = await Task.WhenAny(readTaskHolder.Task, quietDelayTask);

                if (completedTask == readTaskHolder.Task)
                {
                    // Received data — reset quiet period
                    var bytesRead = await readTaskHolder.Task;
                    readTaskHolder.Task = null;
                    if (bytesRead == 0) break;
                    parser.Feed(Encoding.UTF8.GetString(readBuffer, 0, bytesRead));
                }
                else
                {
                    // No data arrived within quietPeriod — terminal is idle
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // maxWait reached or external cancellation
        }
    }

    /// <summary>
    /// Wait for the cursor to move past a specific line.
    /// Stores the pending read task in the holder so it can carry over to the next phase.
    /// </summary>
    private static async Task WaitForCursorMoveAsync(
        IPtyConnection pty,
        byte[] readBuffer,
        VtSequenceParser parser,
        int startLine,
        TimeSpan timeout,
        ReadTaskHolder readTaskHolder,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            while (!linkedToken.IsCancellationRequested)
            {
                if (parser.Buffer.CursorY > startLine) return;

                readTaskHolder.Task ??= pty.ReaderStream.ReadAsync(readBuffer, linkedToken).AsTask();
                var pollDelayTask = Task.Delay(100, linkedToken);

                var completedTask = await Task.WhenAny(readTaskHolder.Task, pollDelayTask);
                if (completedTask == readTaskHolder.Task)
                {
                    var bytesRead = await readTaskHolder.Task;
                    readTaskHolder.Task = null;
                    if (bytesRead == 0) break;
                    parser.Feed(Encoding.UTF8.GetString(readBuffer, 0, bytesRead));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancellation
        }
    }

    /// <summary>
    /// Simple holder to pass a mutable Task reference between async methods.
    /// </summary>
    private sealed class ReadTaskHolder
    {
        public Task<int>? Task { get; set; }
    }
}