using Avalonia;
using Everywhere.Linux.Desktop;

namespace Everywhere.Linux.X11;

/// <summary>
/// Restores the old Linux branch's EWMH window/PID/frame design through the current
/// desktop contract. The starter targets one X screen at scale 1; RandR output
/// enumeration and mixed-DPI coordinate normalization remain follow-up work.
/// </summary>
public sealed class X11CompositorStateProvider : ICompositorStateProvider
{
    /// <inheritdoc />
    public string InstanceId => "x11:" + Environment.GetEnvironmentVariable("DISPLAY");

    /// <inheritdoc />
    public WindowStackingOrderKind StackingOrderKind => WindowStackingOrderKind.Compositor;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<OutputInfo>> GetOutputsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = new X11Connection();
        return ValueTask.FromResult<IReadOnlyList<OutputInfo>>([new(connection.OutputName, connection.Bounds, 1)]);
    }

    /// <inheritdoc />
    public ValueTask<PixelPoint> GetGlobalCursorPositionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = new X11Connection();
        return ValueTask.FromResult(connection.GetPointer());
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CompositorWindowInfo>> GetWindowsInStackingOrderAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = new X11Connection();
        var windows = connection.ReadCardinals(connection.Root, "_NET_CLIENT_LIST_STACKING");
        if (windows.Length > 4096)
        {
            throw new InvalidDataException("The X11 window list exceeded its observation limit.");
        }
        var desktop = connection.ReadCardinals(connection.Root, "_NET_CURRENT_DESKTOP").FirstOrDefault();
        var result = new List<CompositorWindowInfo>();
        foreach (var window in windows.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var observed = ReadWindow(connection, window, desktop);
                if (observed is not null)
                {
                    result.Add(observed);
                }
            }
            catch (InvalidOperationException)
            {
                // A foreign window may disappear between replies.
            }
        }
        return ValueTask.FromResult<IReadOnlyList<CompositorWindowInfo>>(result);
    }

    /// <inheritdoc />
    public ValueTask<CompositorWindowInfo?> GetActiveWindowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = new X11Connection();
        var window = connection.ReadCardinals(connection.Root, "_NET_ACTIVE_WINDOW").FirstOrDefault();
        if (window == 0)
        {
            return ValueTask.FromResult<CompositorWindowInfo?>(null);
        }
        try
        {
            return ValueTask.FromResult(ReadWindow(
                connection,
                window,
                connection.ReadCardinals(connection.Root, "_NET_CURRENT_DESKTOP").FirstOrDefault()));
        }
        catch (InvalidOperationException)
        {
            return ValueTask.FromResult<CompositorWindowInfo?>(null);
        }
    }

    private static CompositorWindowInfo? ReadWindow(X11Connection connection, uint window, uint desktop)
    {
        var types = connection.ReadCardinals(window, "_NET_WM_WINDOW_TYPE");
        if (types.Contains(connection.Atom("_NET_WM_WINDOW_TYPE_DESKTOP")) ||
            types.Contains(connection.Atom("_NET_WM_WINDOW_TYPE_DOCK")))
        {
            return null;
        }
        var states = connection.ReadCardinals(window, "_NET_WM_STATE");
        var workspace = connection.ReadCardinals(window, "_NET_WM_DESKTOP");
        if (states.Contains(connection.Atom("_NET_WM_STATE_HIDDEN")) ||
            workspace.Length > 0 && workspace[0] != uint.MaxValue && workspace[0] != desktop)
        {
            return null;
        }
        if (connection.GetClientBounds(window) is not { Width: > 0, Height: > 0 } client)
        {
            return null;
        }
        var extents = connection.ReadCardinals(window, "_NET_FRAME_EXTENTS");
        var frame = extents.Length == 4 ? new PixelRect(
            client.X - checked((int)extents[0]),
            client.Y - checked((int)extents[2]),
            checked(client.Width + (int)extents[0] + (int)extents[1]),
            checked(client.Height + (int)extents[2] + (int)extents[3])) : (PixelRect?)null;
        var pid = connection.ReadCardinals(window, "_NET_WM_PID").FirstOrDefault();
        var title = connection.ReadText(window, "_NET_WM_NAME");
        if (string.IsNullOrEmpty(title))
        {
            title = connection.ReadText(window, "WM_NAME");
        }
        var application = connection.ReadText(window, "WM_CLASS").Split('\0').LastOrDefault() ?? string.Empty;
        return new(
            window.ToString("X"),
            (nint)window,
            checked((int)pid),
            application,
            title,
            connection.OutputName,
            client,
            frame,
            states.Contains(connection.Atom("_NET_WM_STATE_FULLSCREEN")));
    }
}