using System.Reflection;
using System.Threading.Channels;
using Avalonia.Input;
using Everywhere.Interop;
using Everywhere.Linux.Desktop;
using Microsoft.Extensions.Logging.Abstractions;
using Tmds.DBus.Protocol;

namespace Everywhere.Linux.Tests;

[TestFixture, NonParallelizable]
[Explicit("Run on a private dbus-run-session bus with EVERYWHERE_PORTAL_TEST_BUS set to its address.")]
public sealed class PortalShortcutSessionTests
{
    private string? _previousDesktop;
    private string? _previousKdeVersion;

    [SetUp]
    public void RequirePrivateBus()
    {
        _previousDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        _previousKdeVersion = Environment.GetEnvironmentVariable("KDE_SESSION_VERSION");
        var address = Environment.GetEnvironmentVariable("EVERYWHERE_PORTAL_TEST_BUS");
        Assert.That(address, Is.Not.Null.And.Not.Empty, "These tests must never use the desktop's real portal.");
        Assert.That(DBusAddress.Session, Is.EqualTo(address));
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "KDE");
        Environment.SetEnvironmentVariable("KDE_SESSION_VERSION", "6");
    }

    [TearDown]
    public void RestoreDesktopEnvironment()
    {
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", _previousDesktop);
        Environment.SetEnvironmentVariable("KDE_SESSION_VERSION", _previousKdeVersion);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PendingPermission_StartRecording_ClosesSessionAndRestoresSameRegistration(bool shouldEndRecordingImmediately)
    {
        using var portal = await PendingPortal.StartAsync();
        await using var listener = new PortalShortcutListener(NullLogger<PortalShortcutListener>.Instance);
        using var registration = listener.Register(new KeyboardShortcut(Key.K, KeyModifiers.Control), () => { });
        var firstSession = await portal.Bindings.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        using var recording = listener.StartCaptureKeyboardShortcut();
        if (shouldEndRecordingImmediately)
        {
            recording.Dispose();
        }
        var closed = await portal.Closures.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(closed, Is.EqualTo(firstSession.SessionPath));

        recording.Dispose();
        var restored = await portal.Bindings.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Multiple(() =>
        {
            Assert.That(restored.SessionPath, Is.Not.EqualTo(firstSession.SessionPath));
            Assert.That(firstSession.Shortcuts, Is.EquivalentTo(new Dictionary<string, string>
            {
                [$"keyboard_{(int)KeyModifiers.Control}_{(int)Key.K}"] = "CTRL+k"
            }));
            Assert.That(restored.Shortcuts, Is.EquivalentTo(firstSession.Shortcuts));
        });
    }

    [Test]
    public async Task PendingPermission_RemoveRegistration_ClosesSessionWithoutWaitingForApproval()
    {
        using var portal = await PendingPortal.StartAsync();
        await using var listener = new PortalShortcutListener(NullLogger<PortalShortcutListener>.Instance);
        using var registration = listener.Register(new KeyboardShortcut(Key.K, KeyModifiers.Control), () => { });
        var session = await portal.Bindings.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        registration.Dispose();
        var closed = await portal.Closures.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(closed, Is.EqualTo(session.SessionPath));
    }

    [Test]
    public async Task GrantedShortcut_DisposeWhileActivationIsQueued_DiscardsTheCallback()
    {
        using var portal = await PendingPortal.StartAsync(shouldGrant: true);
        await using var listener = new PortalShortcutListener(NullLogger<PortalShortcutListener>.Instance);
        var shortcut = new KeyboardShortcut(Key.K, KeyModifiers.Control);
        var firstActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var registration = listener.Register(shortcut, () =>
        {
            Interlocked.Increment(ref calls);
            firstActivation.TrySetResult();
        });
        var binding = await portal.Bindings.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await portal.ActivateAndDrainReaderAsync(binding);
        await firstActivation.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var gateField = typeof(PortalShortcutListener).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(PortalShortcutListener), "_gate");
        var gate = gateField.GetValue(listener) as Lock ?? throw new InvalidOperationException("Missing listener lock.");
        await Task.Factory.StartNew(() =>
        {
            // Hold application dispatch while the real session receives and queues its signal.
            // A dedicated thread can synchronously dispose without blocking the D-Bus reader.
            lock (gate)
            {
                portal.ActivateAndDrainReaderAsync(binding).GetAwaiter().GetResult();
                listener.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(5));

        await Task.Delay(100);
        Assert.That(Volatile.Read(ref calls), Is.EqualTo(1));
        Assert.Throws<ObjectDisposedException>(() => listener.Register(shortcut, () => { }));
    }

    private sealed record ShortcutBinding(string SessionPath, string Sender, IReadOnlyDictionary<string, string> Shortcuts);

    // This portal deliberately never approves BindShortcuts by default. The real listener must
    // cancel its outstanding request and close the session through the public protocol.
    private sealed class PendingPortal(DBusConnection connection, bool shouldGrant) : IPathMethodHandler, IDisposable
    {
        public string Path => "/org/freedesktop/portal/desktop";
        public bool HandlesChildPaths => true;
        public Channel<ShortcutBinding> Bindings { get; } = Channel.CreateUnbounded<ShortcutBinding>();
        public Channel<string> Closures { get; } = Channel.CreateUnbounded<string>();

        public static async Task<PendingPortal> StartAsync(bool shouldGrant = false)
        {
            var address = Environment.GetEnvironmentVariable("EVERYWHERE_PORTAL_TEST_BUS")
                ?? throw new InvalidOperationException("The private test bus is unavailable.");
            var connection = new DBusConnection(address);
            try
            {
                await connection.ConnectAsync();
                var portal = new PendingPortal(connection, shouldGrant);
                connection.AddMethodHandler(portal);
                if (!await connection.TryRequestNameAsync("org.freedesktop.portal.Desktop", RequestNameOptions.None))
                {
                    throw new InvalidOperationException("The test bus already has a desktop portal.");
                }
                return portal;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            switch (context.Request.MemberAsString)
            {
                case "Register":
                    ReplyEmptyResponse(context);
                    break;
                case "CreateSession":
                    HandleCreateSession(context);
                    break;
                case "BindShortcuts":
                    HandleBindShortcuts(context);
                    break;
                case "Close":
                    ReplyEmptyResponse(context);
                    Closures.Writer.TryWrite(context.Request.PathAsString ?? throw new InvalidDataException("Missing session path."));
                    break;
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
            return ValueTask.CompletedTask;
        }

        public async Task ActivateAndDrainReaderAsync(ShortcutBinding binding)
        {
            using (var writer = connection.GetMessageWriter())
            {
                writer.WriteSignalHeader(binding.Sender, Path, "org.freedesktop.portal.GlobalShortcuts", "Activated", "osta{sv}");
                writer.WriteObjectPath(binding.SessionPath);
                writer.WriteString(binding.Shortcuts.Keys.Single());
                writer.WriteUInt64(0);
                writer.WriteDictionary(new Dictionary<string, VariantValue>());
                connection.TrySendMessage(writer.CreateMessage());
            }

            // The reply follows the activation on the same bus connection. Once received,
            // the client's reader has processed the signal and queued application dispatch.
            var ping = CreatePeerPing(binding.Sender);
            try
            {
                await connection.CallMethodAsync(ping).WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (DBusErrorReplyException exception) when (
                exception.ErrorName is "org.freedesktop.DBus.Error.UnknownMethod" or "org.freedesktop.DBus.Error.UnknownObject")
            {
                // A client without an exported peer still replies after reading the preceding signal.
            }
        }

        public void Dispose() => connection.Dispose();

        private MessageBuffer CreatePeerPing(string sender)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(sender, "/", "org.freedesktop.DBus.Peer", "Ping");
            return writer.CreateMessage();
        }

        private void HandleCreateSession(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var options = reader.ReadDictionaryOfStringToVariantValue();
            var sender = context.Request.SenderAsString ?? throw new InvalidDataException("Missing bus sender.");
            var token = options["handle_token"].GetString();
            var requestPath = Path + "/request/" + sender.TrimStart(':').Replace('.', '_') + "/" + token;
            var sessionPath = Path + "/session/" + token;
            using (var writer = context.CreateReplyWriter("o"))
            {
                writer.WriteObjectPath(requestPath);
                context.Reply(writer.CreateMessage());
            }

            using (var writer = connection.GetMessageWriter())
            {
                writer.WriteSignalHeader(sender, requestPath, "org.freedesktop.portal.Request", "Response", "ua{sv}");
                writer.WriteUInt32(0);
                writer.WriteDictionary(new Dictionary<string, VariantValue>
                {
                    ["session_handle"] = sessionPath,
                });
                connection.TrySendMessage(writer.CreateMessage());
            }
        }

        private void HandleBindShortcuts(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var sessionPath = reader.ReadObjectPath().ToString();
            var shortcuts = new Dictionary<string, string>();
            var array = reader.ReadArrayStart(DBusType.Struct);
            while (reader.HasNext(array))
            {
                reader.AlignStruct();
                var id = reader.ReadString();
                var properties = reader.ReadDictionaryOfStringToVariantValue();
                shortcuts.Add(id, properties["preferred_trigger"].GetString());
            }
            reader.ReadString(); // Parent window.
            var options = reader.ReadDictionaryOfStringToVariantValue();
            var sender = context.Request.SenderAsString ?? throw new InvalidDataException("Missing bus sender.");
            var requestPath = Path + "/request/" + sender.TrimStart(':').Replace('.', '_') + "/" + options["handle_token"].GetString();
            using (var writer = context.CreateReplyWriter("o"))
            {
                writer.WriteObjectPath(requestPath);
                context.Reply(writer.CreateMessage());
            }

            if (shouldGrant)
            {
                var granted = new Tmds.DBus.Protocol.Array<Struct<string, Dict<string, VariantValue>>>();
                foreach (var shortcut in shortcuts)
                {
                    granted.Add(Struct.Create(shortcut.Key, new Dict<string, VariantValue>
                    {
                        ["trigger_description"] = shortcut.Value
                    }));
                }
                using var writer = connection.GetMessageWriter();
                writer.WriteSignalHeader(sender, requestPath, "org.freedesktop.portal.Request", "Response", "ua{sv}");
                writer.WriteUInt32(0);
                writer.WriteDictionary(new Dictionary<string, VariantValue> { ["shortcuts"] = granted });
                connection.TrySendMessage(writer.CreateMessage());
            }

            Bindings.Writer.TryWrite(new ShortcutBinding(sessionPath, sender, shortcuts));
        }

        private static void ReplyEmptyResponse(MethodContext context)
        {
            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());
        }
    }
}
