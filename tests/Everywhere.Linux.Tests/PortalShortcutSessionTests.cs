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
    [SetUp]
    public void RequirePrivateBus()
    {
        var address = Environment.GetEnvironmentVariable("EVERYWHERE_PORTAL_TEST_BUS");
        Assert.That(address, Is.Not.Null.And.Not.Empty, "These tests must never use the desktop's real portal.");
        Assert.That(DBusAddress.Session, Is.EqualTo(address));
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
        Assert.That(closed, Is.EqualTo(firstSession));

        recording.Dispose();
        var restored = await portal.Bindings.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(restored, Is.Not.EqualTo(firstSession));
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
        Assert.That(closed, Is.EqualTo(session));
    }

    // This portal deliberately never approves BindShortcuts. The real listener must
    // cancel its outstanding request and close the session through the public protocol.
    private sealed class PendingPortal(DBusConnection connection) : IPathMethodHandler, IDisposable
    {
        public string Path => "/org/freedesktop/portal/desktop";
        public bool HandlesChildPaths => true;
        public Channel<string> Bindings { get; } = Channel.CreateUnbounded<string>();
        public Channel<string> Closures { get; } = Channel.CreateUnbounded<string>();

        public static async Task<PendingPortal> StartAsync()
        {
            var address = Environment.GetEnvironmentVariable("EVERYWHERE_PORTAL_TEST_BUS")
                ?? throw new InvalidOperationException("The private test bus is unavailable.");
            var connection = new DBusConnection(address);
            try
            {
                await connection.ConnectAsync();
                var portal = new PendingPortal(connection);
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

        public void Dispose() => connection.Dispose();

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
            using (var writer = context.CreateReplyWriter("o"))
            {
                writer.WriteObjectPath(Path + "/request/pending");
                context.Reply(writer.CreateMessage());
            }

            // Reply with the request handle, but deliberately leave approval pending.
            Bindings.Writer.TryWrite(sessionPath);
        }

        private static void ReplyEmptyResponse(MethodContext context)
        {
            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());
        }
    }
}
