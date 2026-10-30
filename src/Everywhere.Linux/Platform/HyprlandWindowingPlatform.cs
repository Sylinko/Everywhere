using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Everywhere.Linux.Hyprland;

namespace Everywhere.Linux.Platform;

/// <summary>
/// Supplies Hyprland's global geometry to the UI backend. Native Wayland deliberately
/// has no global position, move, activation or topmost API; those operations belong
/// to this explicitly selected compositor rather than to framework scale factors.
/// </summary>
public sealed partial class HyprlandWindowingPlatform(IWindowingPlatform inner) : IWindowingPlatform
{
    private readonly HyprlandConnection _connection = new();
    private readonly List<HyprlandWindowState> _visible = [];
    private readonly Dictionary<HyprlandWindowState, string> _pendingHides = [];
    private readonly DispatcherTimer _timer = new()
    {
        Interval = TimeSpan.FromMilliseconds(60)
    };
    private int _nextId;
    private bool _hasTimerHandler;
    private string? _activeWindow;

    /// <summary>
    /// Decorates the initialized backend before any application windows are created.
    /// </summary>
    public static void Install()
    {
        var platform = AvaloniaLocator.Current.GetRequiredService<IWindowingPlatform>();
        Serilog.Log.Information("Initialized Hyprland UI bridge over {WindowingBackend}.", platform.GetType().FullName);
        AvaloniaLocator.CurrentMutable.Bind<IWindowingPlatform>().ToConstant(new HyprlandWindowingPlatform(platform));
    }

    /// <inheritdoc />
    public IWindowImpl CreateWindow() => new WindowImpl(inner.CreateWindow(), this, ++_nextId);

    /// <inheritdoc />
    public ITopLevelImpl CreateEmbeddableTopLevel() => inner.CreateEmbeddableTopLevel();

    /// <inheritdoc />
    public IWindowImpl CreateEmbeddableWindow() => inner.CreateEmbeddableWindow();

    /// <inheritdoc />
    public ITrayIconImpl? CreateTrayIcon() => inner.CreateTrayIcon();

    /// <inheritdoc />
    public void GetWindowsZOrder(ReadOnlySpan<IWindowImpl> windows, Span<long> zOrder)
    {
        var originals = windows.ToArray().Select(item => item is WindowImpl wrapped ? wrapped.Inner : item).ToArray();
        inner.GetWindowsZOrder(originals, zOrder);
    }

    /// <summary>
    /// Starts compositor observation for a visible owned surface.
    /// </summary>
    public void Track(HyprlandWindowState state)
    {
        _pendingHides.Remove(state);
        if (!_visible.Contains(state))
        {
            _visible.Add(state);
        }

        if (!_hasTimerHandler)
        {
            _timer.Tick += (_, _) => Refresh();
            _hasTimerHandler = true;
        }

        _timer.Start();
        Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Stops observation and retains the mapped address until its hide is acknowledged.
    /// </summary>
    public void Untrack(HyprlandWindowState state)
    {
        if (state.Address is { } address)
        {
            _pendingHides[state] = address;
        }

        _visible.Remove(state);
        if (_visible.Count == 0)
        {
            _timer.Stop();
        }
    }

    /// <summary>
    /// Waits for the compositor to consume queued Wayland unmaps before another
    /// connection requests pixels. A UI dispatcher turn is not that barrier.
    /// </summary>
    public async Task WaitForHiddenSurfacesAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        while (_pendingHides.Count > 0)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var clients = _connection.Read<HyprlandWindow[]>("clients");
            foreach (var pair in _pendingHides.ToArray())
            {
                if (!clients.Any(window => window.IsMapped && window.Address == pair.Value))
                {
                    _pendingHides.Remove(pair.Key);
                }
            }

            if (_pendingHides.Count > 0)
            {
                await Task.Delay(16, deadline.Token);
            }
        }
    }

    private void Refresh()
    {
        if (_visible.Count == 0)
        {
            return;
        }
        try
        {
            var clients = _connection.Read<HyprlandWindow[]>("clients");
            foreach (var state in _visible.ToArray())
            {
                state.Observe(clients);
            }

            var activeWindow = clients.FirstOrDefault(item => item.FocusHistoryId == 0)?.Address;
            if (_activeWindow == activeWindow)
            {
                return;
            }

            _activeWindow = activeWindow;
            // wl_shell/xdg_shell do not provide an always-on-top request. Only
            // re-raise owned topmost surfaces when compositor focus changes.
            foreach (var state in _visible.Where(item => item.IsTopmost))
            {
                state.Raise();
            }
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or TimeoutException or InvalidOperationException)
        {
            Serilog.Log.Debug(exception, "The compositor geometry observation is temporarily unavailable.");
        }
    }

}