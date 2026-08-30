using System.Text.Json;

namespace Everywhere.Automation.TestApp;

/// <summary>
/// Reads controller commands on a dedicated background thread and publishes target status lines atomically.
/// </summary>
public sealed class TestAppControlChannel
{
    /// <summary>
    /// Occurs when one valid command is received from the controller.
    /// </summary>
    public event Action<TestAppCommand>? CommandReceived;

    /// <summary>
    /// Occurs when an input line cannot be parsed as a protocol command.
    /// </summary>
    public event Action<Exception>? ProtocolError;

    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly Lock _writeLock = new();
    private Thread? _readerThread;

    /// <summary>
    /// Initializes a channel over the supplied streams. Standard input and output are used by default.
    /// </summary>
    public TestAppControlChannel(TextReader? reader = null, TextWriter? writer = null)
    {
        _reader = reader ?? Console.In;
        _writer = writer ?? Console.Out;
    }

    /// <summary>
    /// Starts the background command reader exactly once.
    /// </summary>
    public void Start()
    {
        if (_readerThread is not null)
        {
            throw new InvalidOperationException("The TestApp control channel has already started.");
        }

        _readerThread = new Thread(ReadCommands)
        {
            IsBackground = true,
            Name = "Visual Context TestApp Control",
        };
        _readerThread.Start();
    }

    /// <summary>
    /// Writes one complete compact JSON status line without interleaving concurrent publishers.
    /// </summary>
    public void Publish(TestAppStatus status)
    {
        lock (_writeLock)
        {
            _writer.WriteLine(TestAppProtocol.Serialize(status));
            _writer.Flush();
        }
    }

    private void ReadCommands()
    {
        string? line;
        while ((line = _reader.ReadLine()) is not null)
        {
            try
            {
                CommandReceived?.Invoke(TestAppProtocol.Deserialize<TestAppCommand>(line));
            }
            catch (JsonException exception)
            {
                ProtocolError?.Invoke(exception);
            }
        }
    }
}
