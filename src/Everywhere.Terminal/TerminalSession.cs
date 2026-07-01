using System.Text;
using Porta.Pty;

namespace Everywhere.Terminal;

internal enum TerminalReadOutcome
{
    Data,
    Idle,
    EndOfStream,
}

/// <summary>
/// Shared state and IO helpers for a single PTY-backed terminal session.
/// </summary>
public sealed class TerminalSession
{
    public IPtyConnection Pty { get; }

    public TerminalDimensions Dimensions { get; private set; }

    public TerminalParser Parser { get; }

    public PtyTextDecoder TextDecoder { get; }

    public TerminalResponseWriter ResponseWriter { get; }

    public byte[] ReadBuffer { get; } = new byte[ReadBufferSize];

    private const int ReadBufferSize = 4096;
    private static readonly TimeSpan BracketedPasteProbeIdlePeriod = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan BracketedPasteProbeTimeout = TimeSpan.FromSeconds(3);

    private Task<int>? _pendingReadTask;
    private bool? _isBracketedPasteInputSupported;

    public TerminalSession(IPtyConnection pty, TerminalDimensions dimensions)
    {
        Pty = pty;
        Dimensions = dimensions;
        TextDecoder = new PtyTextDecoder(ReadBufferSize);
        ResponseWriter = new TerminalResponseWriter(pty);
        Parser = new TerminalParser(
            terminalResponseHandler: ResponseWriter.Queue,
            dimensions: dimensions);
    }

    public static TerminalSession FromPtyOptions(IPtyConnection pty, PtyOptions options)
    {
        return new TerminalSession(pty, TerminalDimensions.FromPtyOptions(options));
    }

    internal async ValueTask<TerminalReadOutcome> ReadOrIdleAsync(
        TimeSpan idlePeriod,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var readTask = _pendingReadTask ??= Pty.ReaderStream.ReadAsync(ReadBuffer, CancellationToken.None).AsTask();
        if (readTask.IsCompleted)
        {
            return await CompleteReadAsync(readTask, cancellationToken);
        }

        var delayTask = Task.Delay(idlePeriod, cancellationToken);
        var completedTask = await Task.WhenAny(readTask, delayTask);
        if (completedTask != readTask)
        {
            await delayTask;
            return TerminalReadOutcome.Idle;
        }

        return await CompleteReadAsync(readTask, cancellationToken);
    }

    internal async Task WaitForIdleAsync(
        TimeSpan maxWait,
        TimeSpan quietPeriod,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < maxWait)
        {
            var remaining = maxWait - (DateTimeOffset.UtcNow - startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var idlePeriod = remaining < quietPeriod ? remaining : quietPeriod;
            var outcome = await ReadOrIdleAsync(idlePeriod, cancellationToken);
            if (outcome != TerminalReadOutcome.Data)
            {
                break;
            }
        }
    }

    public ValueTask FlushTerminalResponsesAsync(CancellationToken cancellationToken)
    {
        return ResponseWriter.FlushAsync(cancellationToken);
    }

    public async ValueTask WriteInputAsync(string input, CancellationToken cancellationToken = default)
    {
        if (input.Length == 0) return;

        await Pty.WriterStream.WriteAsync(Encoding.UTF8.GetBytes(input), cancellationToken);
        await Pty.WriterStream.FlushAsync(cancellationToken);
    }

    public async ValueTask WritePasteAsync(string text, CancellationToken cancellationToken = default)
    {
        if (text.Length == 0) return;

        if (Parser.IsBracketedPasteModeEnabled)
        {
            await WriteInputAsync("\e[200~", cancellationToken);
            await WriteInputAsync(text.Replace("\r\n", "\n").Replace('\r', '\n'), cancellationToken);
            await WriteInputAsync("\e[201~", cancellationToken);
            return;
        }

        await WriteInputAsync(text.Replace("\r\n", "\r").Replace('\n', '\r'), cancellationToken);
    }

    public async Task<bool> IsBracketedPasteInputSupportedAsync(ShellType shellType, CancellationToken cancellationToken = default)
    {
        if (Parser.IsBracketedPasteModeEnabled)
        {
            _isBracketedPasteInputSupported = true;
            return true;
        }

        if (_isBracketedPasteInputSupported is { } cached)
        {
            return cached;
        }

        if (shellType is not (ShellType.PowerShell or ShellType.Bash or ShellType.Zsh))
        {
            _isBracketedPasteInputSupported = false;
            return false;
        }

        var supported = await ProbeBracketedPasteInputAsync(shellType, cancellationToken);
        _isBracketedPasteInputSupported = supported;
        return supported;
    }

    public async ValueTask ResizeAsync(TerminalDimensions dimensions, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        dimensions = new TerminalDimensions(dimensions.Columns, dimensions.Rows);
        if (dimensions == Dimensions) return;

        Pty.Resize(dimensions.Columns, dimensions.Rows);
        Dimensions = dimensions;
        Parser.Resize(dimensions);

        await FlushTerminalResponsesAsync(cancellationToken);
    }

    private async ValueTask<TerminalReadOutcome> CompleteReadAsync(Task<int> readTask, CancellationToken cancellationToken)
    {
        try
        {
            var bytesRead = await readTask;
            if (bytesRead == 0)
            {
                Parser.Feed(TextDecoder.Flush());
                await FlushTerminalResponsesAsync(cancellationToken);
                return TerminalReadOutcome.EndOfStream;
            }

            Parser.Feed(TextDecoder.Decode(ReadBuffer.AsSpan(0, bytesRead)));
            await FlushTerminalResponsesAsync(cancellationToken);
            return TerminalReadOutcome.Data;
        }
        finally
        {
            if (ReferenceEquals(_pendingReadTask, readTask))
            {
                _pendingReadTask = null;
            }
        }
    }

    private async Task<bool> ProbeBracketedPasteInputAsync(ShellType shellType, CancellationToken cancellationToken)
    {
        var commandFinishedCount = 0;
        var commandReadyAfterFinishCount = 0;

        void HandleMarker(in ShellIntegrationMarker marker)
        {
            switch (marker.Type)
            {
                case ShellIntegrationMarkerType.CommandFinished:
                    commandFinishedCount++;
                    break;
                case ShellIntegrationMarkerType.CommandReady when commandFinishedCount > 0:
                    commandReadyAfterFinishCount++;
                    break;
            }
        }

        var probe = BuildBracketedPasteProbe(shellType);

        Parser.ShellIntegrationMarkerReceived += HandleMarker;
        try
        {
            await WriteInputAsync("\e[200~", cancellationToken);
            await WriteInputAsync(probe, cancellationToken);
            await WriteInputAsync("\e[201~\r", cancellationToken);

            using var timeoutCts = new CancellationTokenSource(BracketedPasteProbeTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var linkedToken = linkedCts.Token;

            try
            {
                while (!linkedToken.IsCancellationRequested)
                {
                    var outcome = await ReadOrIdleAsync(BracketedPasteProbeIdlePeriod, linkedToken);
                    if (outcome == TerminalReadOutcome.EndOfStream)
                    {
                        break;
                    }

                    if (outcome == TerminalReadOutcome.Idle && commandFinishedCount > 0)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            return commandFinishedCount == 1 && commandReadyAfterFinishCount >= 1;
        }
        finally
        {
            Parser.ShellIntegrationMarkerReceived -= HandleMarker;
        }
    }

    private static string BuildBracketedPasteProbe(ShellType shellType)
    {
        var nonce = Guid.NewGuid().ToString("N")[..8];
        return shellType switch
        {
            ShellType.PowerShell => $"[void]\"__EVERYWHERE_BP_PROBE_{nonce}_A\"\n[void]\"__EVERYWHERE_BP_PROBE_{nonce}_B\"",
            ShellType.Bash or ShellType.Zsh => $": __EVERYWHERE_BP_PROBE_{nonce}_A\n: __EVERYWHERE_BP_PROBE_{nonce}_B",
            _ => throw new ArgumentOutOfRangeException(nameof(shellType), shellType, null)
        };
    }
}
