using System.Text;
using Porta.Pty;

namespace Everywhere.Terminal;

/// <summary>
/// Buffers terminal query responses produced while parsing PTY output and writes
/// them back to the child process input stream.
/// </summary>
public sealed class TerminalResponseWriter(IPtyConnection pty)
{
    private readonly List<string> _responses = [];

    public void Queue(string response)
    {
        if (!string.IsNullOrEmpty(response))
        {
            _responses.Add(response);
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_responses.Count == 0)
        {
            return;
        }

        foreach (var response in _responses)
        {
            await pty.WriterStream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        }

        _responses.Clear();
        await pty.WriterStream.FlushAsync(cancellationToken);
    }
}
