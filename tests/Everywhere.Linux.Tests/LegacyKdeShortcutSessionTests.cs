using System.Threading.Channels;
using Avalonia.Input;
using Everywhere.Interop;
using Everywhere.Linux.Desktop;
using Microsoft.Extensions.Logging.Abstractions;
using Tmds.DBus.Protocol;

namespace Everywhere.Linux.Tests;

[TestFixture, NonParallelizable]
[Explicit("Run on a private dbus-run-session bus with EVERYWHERE_PORTAL_TEST_BUS set to its address.")]
public sealed class LegacyKdeShortcutSessionTests
{
    private string? _previousDesktop;
    private string? _previousKdeVersion;

    [SetUp]
    public void RequirePrivateBus()
    {
        var address = Environment.GetEnvironmentVariable("EVERYWHERE_PORTAL_TEST_BUS");
        Assert.That(address, Is.Not.Null.And.Not.Empty);
        Assert.That(DBusAddress.Session, Is.EqualTo(address));
        _previousDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        _previousKdeVersion = Environment.GetEnvironmentVariable("KDE_SESSION_VERSION");
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "KDE");
        Environment.SetEnvironmentVariable("KDE_SESSION_VERSION", "5");
    }

    [TearDown]
    public void RestoreDesktopEnvironment()
    {
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", _previousDesktop);
        Environment.SetEnvironmentVariable("KDE_SESSION_VERSION", _previousKdeVersion);
    }

    [Test]
    public async Task Register_CaptureAndDispose_ReleaseOwnedKdeShortcuts()
    {
        using var accelerator = await NativeAccelerator.StartAsync();
        await using var listener = new PortalShortcutListener(NullLogger<PortalShortcutListener>.Instance);
        var activated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = listener.Register(
            new KeyboardShortcut(Key.E, KeyModifiers.Control | KeyModifiers.Shift),
            () => activated.TrySetResult(true));
        var action = await accelerator.Bindings.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        accelerator.Activate(action);
        Assert.That(await activated.Task.WaitAsync(TimeSpan.FromSeconds(3)), Is.True);

        using var recording = listener.StartCaptureKeyboardShortcut();
        var released = await accelerator.Closures.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(released, Is.EqualTo(action));
        recording.Dispose();
        var restored = await accelerator.Bindings.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(restored, Is.EqualTo(action));

        registration.Dispose();
        released = await accelerator.Closures.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(released, Is.EqualTo(action));
    }

    private sealed class NativeAccelerator(DBusConnection connection) : IPathMethodHandler, IDisposable
    {
        public string Path => "/kglobalaccel";
        public bool HandlesChildPaths => false;
        public Channel<string[]> Bindings { get; } = Channel.CreateUnbounded<string[]>();
        public Channel<string[]> Closures { get; } = Channel.CreateUnbounded<string[]>();

        public static async Task<NativeAccelerator> StartAsync()
        {
            var address = Environment.GetEnvironmentVariable("EVERYWHERE_PORTAL_TEST_BUS")
                ?? throw new InvalidOperationException("The private test bus is unavailable.");
            var connection = new DBusConnection(address);
            try
            {
                await connection.ConnectAsync();
                var accelerator = new NativeAccelerator(connection);
                connection.AddMethodHandler(accelerator);
                if (!await connection.TryRequestNameAsync("org.kde.kglobalaccel", RequestNameOptions.None))
                {
                    throw new InvalidOperationException("The private test bus already has a KDE accelerator.");
                }
                return accelerator;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.MemberAsString == "getComponent")
            {
                using var writer = context.CreateReplyWriter("o");
                writer.WriteObjectPath("/component/everywhere_test");
                context.Reply(writer.CreateMessage());
                return ValueTask.CompletedTask;
            }

            var reader = context.Request.GetBodyReader();
            var action = reader.ReadArrayOfString();
            if (context.Request.MemberAsString == "setShortcut")
            {
                var keys = reader.ReadArrayOfInt32();
                using var writer = context.CreateReplyWriter("ai");
                var array = writer.WriteArrayStart(DBusType.Int32);
                foreach (var key in keys)
                {
                    writer.WriteInt32(key);
                }
                writer.WriteArrayEnd(array);
                context.Reply(writer.CreateMessage());
                Bindings.Writer.TryWrite(action);
            }
            else
            {
                using var writer = context.CreateReplyWriter(null);
                context.Reply(writer.CreateMessage());
                if (context.Request.MemberAsString == "unRegister")
                {
                    Closures.Writer.TryWrite(action);
                }
            }
            return ValueTask.CompletedTask;
        }

        public void Activate(string[] action)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteSignalHeader(
                null,
                "/component/everywhere_test",
                "org.kde.kglobalaccel.Component",
                "globalShortcutPressed",
                "ssx");
            writer.WriteString(action[0]);
            writer.WriteString(action[1]);
            writer.WriteInt64(0);
            connection.TrySendMessage(writer.CreateMessage());
        }

        public void Dispose() => connection.Dispose();
    }
}