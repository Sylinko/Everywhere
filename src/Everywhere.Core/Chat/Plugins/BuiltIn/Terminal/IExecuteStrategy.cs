using System.Text;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace Everywhere.Chat.Plugins.BuiltIn.Terminal;

/// <summary>
/// Result of executing a command in the terminal.
/// </summary>
/// <param name="Output">The cleaned command output text.</param>
/// <param name="ExitCode">The exit code of the command, if available.</param>
internal readonly record struct ExecuteResult(string Output, int? ExitCode);

/// <summary>
/// Strategy for executing commands in a PTY and capturing their output.
/// Implementations differ based on whether Shell Integration is available.
/// </summary>
internal interface IExecuteStrategy
{
    /// <summary>
    /// Execute a command in the PTY and return the cleaned output.
    /// </summary>
    /// <param name="pty">The PTY connection to use.</param>
    /// <param name="script">The script to execute.</param>
    /// <param name="isMultiline">Whether the script contains multiple lines.</param>
    /// <param name="timeout">Maximum time to wait for the command to complete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The execution result containing output and exit code.</returns>
    Task<ExecuteResult> ExecuteAsync(
        IPtyConnection pty,
        string script,
        bool isMultiline,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Send a command to the PTY. Each strategy implements its own sending logic:
    /// - Rich: uses bracketed paste mode for multi-line commands (requires Shell Integration + PSReadLine)
    /// - None: sends commands line by line for multi-line commands (no Shell Integration needed)
    /// </summary>
    Task SendCommandAsync(
        IPtyConnection pty,
        string script,
        bool isMultiline,
        CancellationToken cancellationToken);

    /// <summary>
    /// Detect whether Shell Integration is available by reading initial PTY output
    /// and checking for OSC 633 markers. Returns the appropriate strategy.
    /// </summary>
    static async Task<IExecuteStrategy> DetectStrategyAsync(
        IPtyConnection pty,
        ShellType shellType,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var readBuffer = new byte[4096];
        var vtBuffer = new VirtualTerminalBuffer(1024);
        var parser = new VtSequenceParser(vtBuffer);

        // Read initial output for a short period to detect shell integration markers
        var detectionTimeout = TimeSpan.FromSeconds(3);
        using var timeoutCts = new CancellationTokenSource(detectionTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            while (!linkedToken.IsCancellationRequested)
            {
                if (parser.HasDetectedShellIntegration)
                {
                    logger.LogDebug("Shell Integration detected for {ShellType}, using Rich strategy", shellType);
                    return new RichExecuteStrategy(logger);
                }

                using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
                pollCts.CancelAfter(150);

                try
                {
                    var bytesRead = await pty.ReaderStream.ReadAsync(readBuffer, pollCts.Token);
                    if (bytesRead == 0) break;

                    var text = Encoding.UTF8.GetString(readBuffer, 0, bytesRead);
                    parser.Feed(text);
                }
                catch (OperationCanceledException) when (pollCts.IsCancellationRequested && !linkedToken.IsCancellationRequested)
                {
                    // Poll timeout — no new data, keep waiting
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Detection timeout reached
        }

        logger.LogDebug("No Shell Integration detected for {ShellType}, using None strategy", shellType);
        return new NoneExecuteStrategy(logger);
    }
}
