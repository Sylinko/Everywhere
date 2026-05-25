namespace Everywhere.Terminal;

/// <summary>
/// One shell-reported or synthesized command run within a live terminal execution.
/// The output buffer may keep changing until <see cref="WaitAsync"/> completes.
/// </summary>
public sealed class TerminalRun(string commandLine, int maxOutputLines = TerminalLineBuffer.DefaultMaxLines)
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _lock = new();
    private int _isFinished;

    public TerminalLineBuffer Output { get; } = new(maxOutputLines);

    public string OutputText => Output.GetText();

    public string CommandLine { get; private set; } = commandLine;

    public int? ExitCode { get; private set; }

    internal Task Completion => _completion.Task;

    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        return _completion.Task.WaitAsync(cancellationToken);
    }

    internal void SetCommandLine(string commandLine, bool append = false)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return;
        }

        lock (_lock)
        {
            if (!append || CommandLine.Length == 0)
            {
                CommandLine = commandLine;
            }
            else if (!CommandLine.Contains(commandLine, StringComparison.Ordinal))
            {
                CommandLine += "\n" + commandLine;
            }
        }
    }

    internal void Complete(int? exitCode)
    {
        Finish(exitCode);
    }

    internal void Timeout()
    {
        Finish(ExitCode);
    }

    internal void Cancel()
    {
        if (Interlocked.Exchange(ref _isFinished, 1) != 0)
        {
            return;
        }

        _completion.TrySetCanceled();
    }

    internal void Fail(Exception exception)
    {
        if (Interlocked.Exchange(ref _isFinished, 1) != 0)
        {
            return;
        }

        _completion.TrySetException(exception);
    }

    private void Finish(int? exitCode)
    {
        if (Interlocked.Exchange(ref _isFinished, 1) != 0)
        {
            return;
        }

        lock (_lock)
        {
            ExitCode = exitCode;
        }

        _completion.TrySetResult();
    }
}