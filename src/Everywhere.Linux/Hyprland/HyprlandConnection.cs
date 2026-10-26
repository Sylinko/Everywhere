using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Everywhere.Linux.Hyprland;

/// <summary>
/// Talks to one explicitly selected compositor instance. Connections and replies are bounded;
/// the desktop instance is never guessed from the first socket found on disk.
/// </summary>
public sealed class HyprlandConnection
{
    /// <summary>
    /// Gets the explicitly inherited Hyprland instance signature.
    /// </summary>
    public string Instance { get; }

    private readonly string _socketPath;

    /// <summary>
    /// Connects subsequent observations to the inherited Hyprland session.
    /// </summary>
    public HyprlandConnection()
    {
        Instance = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")
            ?? throw new InvalidOperationException("Start Everywhere from the Hyprland session (HYPRLAND_INSTANCE_SIGNATURE is required).");
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
            ?? throw new InvalidOperationException("XDG_RUNTIME_DIR is required.");
        if (Instance.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new InvalidOperationException("Invalid Hyprland instance signature.");
        }

        _socketPath = Path.Combine(runtimeDirectory, "hypr", Instance, ".socket.sock");
    }

    /// <summary>
    /// Reads one compositor JSON observation without invoking a dispatcher.
    /// </summary>
    public T Read<T>(string command)
    {
        using var document = JsonDocument.Parse(Request("j/" + command));
        return document.Deserialize<T>() ?? throw new InvalidDataException("Hyprland returned no observation.");
    }

    /// <summary>
    /// Sends one bounded IPC request to this instance.
    /// </summary>
    public string Request(string request)
    {
        try
        {
            return RequestAsync(request).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException("The Hyprland IPC observation exceeded its deadline.", exception);
        }
    }

    /// <summary>
    /// Applies one explicitly scoped Lua operation and checks the compositor acknowledgement.
    /// </summary>
    public void ExecuteLua(string script)
    {
        var response = Request("/eval " + script).Trim();
        if (response != "ok")
        {
            throw new InvalidOperationException("Hyprland rejected the scoped operation: " + response);
        }
    }

    /// <summary>
    /// Reads one bounded JSON observation, honoring caller cancellation.
    /// </summary>
    public async ValueTask<T> ReadAsync<T>(string command, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = JsonDocument.Parse(await RequestAsync("j/" + command, cancellationToken).ConfigureAwait(false));
            return document.Deserialize<T>() ?? throw new InvalidDataException("Hyprland returned no observation.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Hyprland IPC observation exceeded its deadline.", exception);
        }
    }

    private async Task<string> RequestAsync(string request, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), deadline.Token).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(request);
        var sent = 0;
        while (sent < bytes.Length)
        {
            sent += await socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None, deadline.Token).ConfigureAwait(false);
        }

        using var reply = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var count = await socket.ReceiveAsync(buffer, SocketFlags.None, deadline.Token).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (reply.Length + count > 16 * 1024 * 1024)
            {
                throw new InvalidDataException("Hyprland reply exceeded the observation limit.");
            }

            reply.Write(buffer, 0, count);
        }

        return Encoding.UTF8.GetString(reply.GetBuffer(), 0, (int)reply.Length);
    }
}