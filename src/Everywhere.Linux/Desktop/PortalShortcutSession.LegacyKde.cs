using Avalonia.Input;
using Everywhere.Interop;
using Tmds.DBus.Protocol;

namespace Everywhere.Linux.Desktop;

internal sealed partial class PortalShortcutSession
{
    private static bool IsLegacyKde
    {
        get
        {
            var desktops = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")?.Split(':');
            return Environment.GetEnvironmentVariable("KDE_SESSION_VERSION") == "5" &&
                desktops?.Contains("KDE", StringComparer.OrdinalIgnoreCase) == true;
        }
    }

    private const string KdeShortcutService = "org.kde.kglobalaccel";
    private const string KdeShortcutInterface = "org.kde.KGlobalAccel";
    private const string KdeApplicationId = "com.Sylinko.Everywhere";

    private readonly List<string[]> _legacyKdeActions = [];

    // Plasma 5.27's portal opens System Settings without registering the requested
    // actions. Its native accelerator service supports both X11 and Wayland.
    private async Task InitializeLegacyKdeAsync(
        KeyboardShortcut[] shortcuts,
        Action<string> onActivated,
        CancellationToken cancellationToken)
    {
        foreach (var shortcut in shortcuts)
        {
            var action = new[] { KdeApplicationId, ShortcutId(shortcut), "Everywhere", shortcut.ToString() };
            await _connection.CallMethodAsync(CreateKdeActionRequest("doRegister", action))
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            _legacyKdeActions.Add(action);
        }

        var componentPath = await _connection.CallMethodAsync(
            CreateKdeComponentRequest(),
            static (message, _) => message.GetBodyReader().ReadObjectPath().ToString())
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        _activationWatch = await _connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Sender = KdeShortcutService,
                Path = componentPath,
                Interface = "org.kde.kglobalaccel.Component",
                Member = "globalShortcutPressed",
            },
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (Component: reader.ReadString(), Id: reader.ReadString());
            },
            notification =>
            {
                if (!notification.HasValue || notification.Value.Component != KdeApplicationId)
                {
                    return;
                }

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    if (!Volatile.Read(ref _isDisposed))
                    {
                        onActivated(notification.Value.Id);
                    }
                });
            },
            emitOnCapturedContext: false,
            flags: ObserverFlags.None,
            state: null).ConfigureAwait(false);

        for (var index = 0; index < shortcuts.Length; index++)
        {
            var keys = await _connection.CallMethodAsync(
                CreateKdeActionRequest("setShortcut", _legacyKdeActions[index], ToKdeKey(shortcuts[index])),
                static (message, _) => message.GetBodyReader().ReadArrayOfInt32())
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (keys.Length == 0 || keys.All(key => key == 0))
            {
                throw new InvalidOperationException($"KDE could not register {shortcuts[index]}; the shortcut may already be in use.");
            }
        }

        GrantedShortcutCount = shortcuts.Length;
    }

    private async Task ReleaseLegacyKdeAsync()
    {
        foreach (var action in _legacyKdeActions)
        {
            await _connection.CallMethodAsync(CreateKdeActionRequest("unRegister", action))
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
    }

    private MessageBuffer CreateKdeComponentRequest()
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: KdeShortcutService,
            path: "/kglobalaccel",
            @interface: KdeShortcutInterface,
            member: "getComponent",
            signature: "s");
        writer.WriteString(KdeApplicationId);
        return writer.CreateMessage();
    }

    private MessageBuffer CreateKdeActionRequest(string method, string[] action, int? key = null)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: KdeShortcutService,
            path: "/kglobalaccel",
            @interface: KdeShortcutInterface,
            member: method,
            signature: key is null ? "as" : "asaiu");
        var strings = writer.WriteArrayStart(DBusType.String);
        foreach (var part in action)
        {
            writer.WriteString(part);
        }
        writer.WriteArrayEnd(strings);
        if (key is { } keyValue)
        {
            var keys = writer.WriteArrayStart(DBusType.Int32);
            writer.WriteInt32(keyValue);
            writer.WriteArrayEnd(keys);
            // IsPresent | NoAutoloading: this lease owns the configured key rather
            // than silently loading a different binding from an earlier launch.
            writer.WriteUInt32(6);
        }
        return writer.CreateMessage();
    }

    private static int ToKdeKey(KeyboardShortcut shortcut)
    {
        var symbol = LinuxKeySymbol.FromKey(shortcut.Key);
        var key = shortcut.Key switch
        {
            >= Key.F1 and <= Key.F24 => 0x01000030 + (shortcut.Key - Key.F1),
            Key.Escape => 0x01000000,
            Key.Tab => 0x01000001,
            Key.Back => 0x01000003,
            Key.Return => 0x01000004,
            Key.Insert => 0x01000006,
            Key.Delete => 0x01000007,
            Key.Home => 0x01000010,
            Key.End => 0x01000011,
            Key.Left => 0x01000012,
            Key.Up => 0x01000013,
            Key.Right => 0x01000014,
            Key.Down => 0x01000015,
            Key.PageUp => 0x01000016,
            Key.PageDown => 0x01000017,
            Key.Space => 0x20,
            _ when symbol.Length == 1 => char.ToUpperInvariant(symbol[0]),
            _ => throw new NotSupportedException($"The legacy KDE shortcut backend does not support {shortcut.Key}."),
        };
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Shift))
        {
            key |= 0x02000000;
        }
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Control))
        {
            key |= 0x04000000;
        }
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Alt))
        {
            key |= 0x08000000;
        }
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Meta))
        {
            key |= 0x10000000;
        }
        return key;
    }
}