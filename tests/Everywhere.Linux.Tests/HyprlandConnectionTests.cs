using System.Net.Sockets;
using System.Text;
using Avalonia.Input;
using Everywhere.Linux.Hyprland;
using Everywhere.Linux.Platform;
using Everywhere.ProcessIsolation.Hosts.Input;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.Linux.Tests;

[NonParallelizable]
public sealed class HyprlandConnectionTests
{
    [Test]
    public void DifferentCompositorInstances_GetActivationEndpoint_AreIsolated()
    {
        using var server = new CompositorEndpoint();
        var first = RpcRuntimeIdentity.GetDesktopSessionId();
        Assert.That(first, Is.EqualTo("hyprland:owned-test-instance"));
        Environment.SetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE", "another-test-instance");
        var second = RpcRuntimeIdentity.GetDesktopSessionId();
        Assert.That(ProcessRoleNames.GetApplicationActivationEndpoint(first),
            Is.Not.EqualTo(ProcessRoleNames.GetApplicationActivationEndpoint(second)));
    }

    [TestCase(Key.E, "E")]
    [TestCase(Key.Tab, "Tab")]
    [TestCase(Key.Delete, "Delete")]
    [TestCase(Key.PageDown, "Next")]
    [TestCase(Key.NumPad7, "KP_7")]
    [TestCase(Key.OemMinus, "minus")]
    [TestCase(Key.OemPlus, "equal")]
    public async Task KeyboardShortcut_ApplyState_UsesLuaStringSyntax(Key key, string keySymbol)
    {
        using var server = new CompositorEndpoint();
        var session = new HyprlandInputHostSession();
        try
        {
            var serving = server.ServeAsync(async (socket, token) =>
                await socket.SendAsync(Encoding.UTF8.GetBytes("ok"), SocketFlags.None, token));
            var response = await session.ApplyStateAsync(new ApplyInputStateRequest
            {
                KeyboardRegistrations =
                [
                    new InputKeyboardRegistration
                    {
                        RegistrationId = 1,
                        Key = (int)key,
                        Modifiers = (int)(KeyModifiers.Control | KeyModifiers.Shift),
                    },
                ],
                MouseRegistrations = [],
                CaptureId = 0,
            });
            await serving;
            Assert.That(response.IsApplied, Is.True);
            Assert.That(server.Request, Does.Contain("[\"CTRL + SHIFT + " + keySymbol + "\"]=\""));
            Assert.That(server.Request, Does.Not.Contain("\\u"));
            Assert.That(server.Request, Does.Not.Contain(":remove()"));
            Assert.That(server.Request, Does.Match(@"state\.commands\s*=\s*desired"));
        }
        finally
        {
            var cleanup = server.ServeAsync(async (socket, token) =>
                await socket.SendAsync(Encoding.UTF8.GetBytes("ok"), SocketFlags.None, token));
            await session.DisposeAsync();
            await cleanup;
        }
    }

    [Test]
    public async Task CaptureLease_ApplyAndRelease_PausesAndRestoresRegistrations()
    {
        using var server = new CompositorEndpoint();
        var session = new HyprlandInputHostSession();
        var registrations = typeof(HyprlandInputHostSession).GetField(
            "_registrations",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(HyprlandInputHostSession));
        try
        {
            await ApplyAsync(0);
            Assert.That(registrations.GetValue(session), Is.EquivalentTo(new ulong[] { 1 }));
            await ApplyAsync(42);
            Assert.That(registrations.GetValue(session), Is.Empty);
            await ApplyAsync(0);
            Assert.That(registrations.GetValue(session), Is.EquivalentTo(new ulong[] { 1 }));
        }
        finally
        {
            var cleanup = server.ServeAsync(async (socket, token) =>
                await socket.SendAsync(Encoding.UTF8.GetBytes("ok"), SocketFlags.None, token));
            await session.DisposeAsync();
            await cleanup;
        }

        async Task ApplyAsync(ulong captureId)
        {
            var serving = server.ServeAsync(async (socket, token) =>
                await socket.SendAsync(Encoding.UTF8.GetBytes("ok"), SocketFlags.None, token));
            var response = await session.ApplyStateAsync(new ApplyInputStateRequest
            {
                KeyboardRegistrations =
                [
                    new InputKeyboardRegistration
                    {
                        RegistrationId = 1,
                        Key = (int)Key.E,
                        Modifiers = (int)(KeyModifiers.Control | KeyModifiers.Shift),
                    },
                ],
                MouseRegistrations = [],
                CaptureId = captureId,
            });
            await serving;
            Assert.That(response.IsApplied, Is.True);
        }
    }

    [Test]
    public async Task FragmentedUtf8Reply_Read_ReassemblesOneCompleteObservation()
    {
        using var server = new CompositorEndpoint();
        var serving = server.ServeAsync(async (socket, token) =>
        {
            var reply = Encoding.UTF8.GetBytes("[{\"name\":\"显示器🌏\",\"width\":1920,\"height\":1080,\"scale\":1.5}]");
            foreach (var value in reply)
            {
                await socket.SendAsync(new byte[] { value }, SocketFlags.None, token);
            }
        });
        var result = new HyprlandConnection().Read<HyprlandMonitor[]>("monitors");
        await serving;
        Assert.That(result.Single().Name, Is.EqualTo("显示器🌏"));
        Assert.That(server.Request, Is.EqualTo("j/monitors"));
    }

    [Test]
    public async Task StalledCompositor_Read_ReportsProviderTimeoutRatherThanCallerCancellation()
    {
        using var server = new CompositorEndpoint();
        var serving = server.ServeAsync(async (_, token) => await Task.Delay(Timeout.Infinite, token));
        Assert.Throws<TimeoutException>(() => new HyprlandConnection().Read<HyprlandMonitor[]>("monitors"));
        server.Cancel();
        await serving;
    }

    [Test]
    public async Task RejectedScopedOperation_ExecuteLua_DoesNotReportSuccess()
    {
        using var server = new CompositorEndpoint();
        var serving = server.ServeAsync(async (socket, token) =>
            await socket.SendAsync(Encoding.UTF8.GetBytes("runtime error: rejected"), SocketFlags.None, token));
        Assert.Throws<InvalidOperationException>(() => new HyprlandConnection().ExecuteLua("return false"));
        await serving;
    }

    [Test]
    public async Task StalledCompositor_AsyncRead_PreservesCallerCancellation()
    {
        using var server = new CompositorEndpoint();
        var serving = server.ServeAsync(async (_, token) => await Task.Delay(Timeout.Infinite, token));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new HyprlandConnection().ReadAsync<HyprlandMonitor[]>("monitors", cancellation.Token));
        server.Cancel();
        await serving;
    }

    [Test]
    public async Task StalledCompositor_AsyncRead_DistinguishesProviderDeadline()
    {
        using var server = new CompositorEndpoint();
        var serving = server.ServeAsync(async (_, token) => await Task.Delay(Timeout.Infinite, token));
        Assert.ThrowsAsync<TimeoutException>(async () =>
            await new HyprlandConnection().ReadAsync<HyprlandMonitor[]>("monitors"));
        server.Cancel();
        await serving;
    }

    internal sealed class CompositorEndpoint : IDisposable
    {
        /// <summary>
        /// Gets the request received by the most recent server operation.
        /// </summary>
        public string? Request { get; private set; }

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "everywhere-ipc-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        private readonly string? _oldInstance = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
        private readonly Socket _server = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly CancellationTokenSource _lifetime = new();

        /// <summary>
        /// Creates an isolated compositor socket and selects its desktop environment.
        /// </summary>
        public CompositorEndpoint()
        {
            var directory = Path.Combine(_directory, "hypr", "owned-test-instance");
            Directory.CreateDirectory(directory);
            _server.Bind(new UnixDomainSocketEndPoint(Path.Combine(directory, ".socket.sock")));
            _server.Listen(1);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _directory);
            Environment.SetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE", "owned-test-instance");
        }

        /// <summary>
        /// Accepts one compositor request and sends the supplied response.
        /// </summary>
        public async Task ServeAsync(Func<Socket, CancellationToken, Task> reply)
        {
            try
            {
                using var client = await _server.AcceptAsync(_lifetime.Token);
                var buffer = new byte[4096];
                var count = await client.ReceiveAsync(buffer, SocketFlags.None, _lifetime.Token);
                Request = Encoding.UTF8.GetString(buffer, 0, count);
                await reply(client, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        /// <summary>
        /// Cancels the current server operation.
        /// </summary>
        public void Cancel() => _lifetime.Cancel();

        /// <inheritdoc />
        public void Dispose()
        {
            _lifetime.Cancel();
            _server.Dispose();
            _lifetime.Dispose();
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _oldRuntime);
            Environment.SetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE", _oldInstance);
            Directory.Delete(_directory, true);
        }
    }
}