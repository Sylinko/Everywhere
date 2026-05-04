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
            logger.LogDebug(
                "[Detect] Starting Shell Integration detection for {ShellType} (Timeout: {Timeout}s)",
                shellType,
                detectionTimeout.TotalSeconds);

            while (!linkedToken.IsCancellationRequested)
            {
                var bytesRead = await pty.ReaderStream.ReadAsync(readBuffer, linkedToken);
                if (bytesRead == 0)
                {
                    logger.LogWarning("[Detect] PTY stream closed unexpectedly during detection.");
                    break;
                }

                var text = Encoding.UTF8.GetString(readBuffer, 0, bytesRead);
                parser.Feed(text);

                if (parser.HasDetectedShellIntegration)
                {
                    logger.LogDebug("[Detect] Shell Integration detected for {ShellType}, using Rich strategy", shellType);
                    return new RichExecuteStrategy(logger);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Detection timeout reached
            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("[Detect] Timeout reached. Shell Integration markers not found.");
            }
        }

        logger.LogInformation("[Detect] Falling back to None strategy for {ShellType}.", shellType);
        return new NoneExecuteStrategy(logger);
    }
}