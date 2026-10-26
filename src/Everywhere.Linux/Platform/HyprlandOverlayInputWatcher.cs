using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Everywhere.Extensions;
using Everywhere.Interop;
using Everywhere.Linux.Hyprland;
using Serilog;

namespace Everywhere.Linux.Platform;

/// <summary>
/// Observes dismissal input without consuming it. Keyboard subscriptions exist
/// only while a palette is visible; owned mouse bindings are disabled when idle
/// and reused because this Hyprland version removes bindings by whole chord.
/// Only input categories cross IPC; typed keys and text are never retained.
/// </summary>
public sealed class HyprlandOverlayInputWatcher : OverlayInputWatcherBase
{
    /// <inheritdoc />
    public override bool IsSupported => true;

    private readonly HyprlandConnection _connection = new();
    private readonly string _stateName = "everywhere_palette_" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Environment.ProcessPath ?? "Everywhere")))[..16];
    private CancellationTokenSource? _lifetime;
    private Socket? _socket;
    private Task _worker = Task.CompletedTask;

    /// <inheritdoc />
    protected override void StartWatching()
    {
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _socket = socket;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? throw new InvalidOperationException();
        var path = Path.Combine(runtime, "hypr", _connection.Instance, ".socket2.sock");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        socket.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token).AsTask().GetAwaiter().GetResult();
        var prefix = "everywhere-palette-" + Guid.NewGuid().ToString("N");
        var source = _connection.Read<HyprlandWindow>("activewindow");
        UpdatePointerPosition(ReadPointer());
        _connection.ExecuteLua($$"""
            local state = _G.{{_stateName}}
            if not state then
                state = { mouse = {} }
                _G.{{_stateName}} = state
            end
            state.prefix = '{{prefix}}'
            local function emit(kind)
                if state.prefix then hl.dispatch(hl.dsp.event(state.prefix .. ',' .. kind)) end
            end
            for _, button in ipairs({ 'mouse:272', 'mouse:273', 'mouse:274', 'mouse:275', 'mouse:276', 'mouse_up', 'mouse_down', 'mouse_left', 'mouse_right' }) do
                if not state.mouse[button] then
                    local kind = string.sub(button, 1, 6) == 'mouse:' and 'button' or 'scroll'
                    state.mouse[button] = hl.bind(button, function() emit(kind) end,
                        { non_consuming=true, ignore_mods=true, transparent=true, submap_universal=true })
                end
                state.mouse[button]:set_enabled(true)
            end
            if state.key then state.key:remove() end
            state.key = hl.on('input.keyboard.key', function(key, _, pressed)
                if pressed ~= 1 then return end
                local modifiers = { [37]=true,[50]=true,[62]=true,[64]=true,[92]=true,[105]=true,[108]=true,[133]=true,[134]=true }
                if modifiers[key] then return end
                local navigation = { [9]=true,[23]=true,[36]=true,[65]=true,[110]=true,[111]=true,[112]=true,[113]=true,[114]=true,[115]=true,[116]=true,[117]=true }
                emit(navigation[key] and 'navigation' or 'key')
            end)
            """);
        _worker = ObserveAsync(socket, prefix, source.Address, lifetime);
    }

    /// <inheritdoc />
    protected override void StopWatching()
    {
        try
        {
            _connection.ExecuteLua($$"""
                local state = _G.{{_stateName}}
                if state then
                    state.prefix = nil
                    if state.key then state.key:remove(); state.key = nil end
                    for _, binding in pairs(state.mouse) do binding:set_enabled(false) end
                end
                """);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or InvalidOperationException)
        {
            Log.Debug("Palette input cleanup encountered {FailureType}.", exception.GetType().Name);
        }
        finally
        {
            var lifetime = _lifetime;
            _lifetime = null;
            lifetime?.Cancel();
            _socket?.Dispose();
            _socket = null;
            if (lifetime is not null)
            {
                ReleaseAsync(_worker, lifetime).Detach();
            }

            _worker = Task.CompletedTask;
        }
    }

    private async Task ObserveAsync(Socket socket, string prefix, string sourceAddress, CancellationTokenSource lifetime)
    {
        var events = ReadEventsAsync(socket, "custom>>" + prefix + ",", lifetime.Token);
        var pointer = PollPointerAsync(sourceAddress, lifetime.Token);
        await Task.WhenAny(events, pointer).ConfigureAwait(false);
        var shouldDismiss = !lifetime.IsCancellationRequested;
        lifetime.Cancel();
        try
        {
            await Task.WhenAll(events, pointer).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException or TimeoutException)
        {
            if (shouldDismiss)
            {
                Log.Debug("Palette input observation ended ({FailureType}).", exception.GetType().Name);
            }
        }

        if (shouldDismiss)
        {
            HandleKeyPressed(false);
        }
    }

    private async Task ReadEventsAsync(Socket socket, string prefix, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var line = new byte[512];
        var length = 0;
        var isOversized = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var count = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }
            for (var index = 0; index < count; index++)
            {
                var value = buffer[index];
                if (value == '\n')
                {
                    if (!isOversized)
                    {
                        var message = Encoding.ASCII.GetString(line, 0, length);
                        if (message.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            HandleInput(message[prefix.Length..]);
                        }
                    }
                    length = 0;
                    isOversized = false;
                }
                else if (length < line.Length)
                {
                    line[length++] = value;
                }
                else
                {
                    isOversized = true;
                }
            }
        }
    }

    private async Task PollPointerAsync(string sourceAddress, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(60));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            UpdatePointerPosition(ReadPointer());
            var active = _connection.Read<HyprlandWindow>("activewindow");
            if (active.Address != sourceAddress && active.ProcessId != Environment.ProcessId)
            {
                HandleKeyPressed(false);
            }
        }
    }

    private void HandleInput(string kind)
    {
        switch (kind)
        {
            case "button":
                HandlePointerPressed(ReadPointer());
                break;
            case "scroll":
                HandlePointerScrolled(ReadPointer());
                break;
            case "navigation":
                HandleKeyPressed(true);
                break;
            case "key":
                HandleKeyPressed(false);
                break;
        }
    }

    private PixelPoint ReadPointer()
    {
        var point = _connection.Read<Dictionary<string, int>>("cursorpos");
        return new PixelPoint(point["x"], point["y"]);
    }

    private static async Task ReleaseAsync(Task worker, CancellationTokenSource lifetime)
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        finally
        {
            lifetime.Dispose();
        }
    }
}