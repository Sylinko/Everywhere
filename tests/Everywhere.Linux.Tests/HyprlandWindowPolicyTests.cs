using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Platform;
using Everywhere.Linux.Platform;
using Everywhere.Linux.Hyprland;

namespace Everywhere.Linux.Tests;

[NonParallelizable]
public sealed class HyprlandWindowPolicyTests
{
    [TestCase(false, false, "true", "true")]
    [TestCase(false, true, "false", "true")]
    [TestCase(true, true, "false", "false")]
    public async Task OwnedWindow_ApplyPolicy_OnlyPassiveEffectsForbidFocus(
        bool canActivate, bool isHitTestVisible, string noFocus, string noInitialFocus)
    {
        using var endpoint = new HyprlandConnectionTests.CompositorEndpoint();
        var state = new HyprlandWindowState(DispatchProxy.Create<IWindowImpl, NativeWindowStub>(), false, 1, _ => { }, _ => { })
        {
            IsFloating = true,
            CanActivate = canActivate,
            IsHitTestVisible = isHitTestVisible,
        };
        try
        {
            var serving = endpoint.ServeAsync(ReplyAsync);
            state.BeforeShow(new Size(300, 60));
            await serving;
            Assert.That(endpoint.Request, Does.Contain($"no_focus={noFocus},no_initial_focus={noInitialFocus}"));
        }
        finally
        {
            var cleanup = endpoint.ServeAsync(ReplyAsync);
            state.Dispose();
            await cleanup;
        }
    }

    [Test]
    public async Task PassiveEffect_BecomesClickableAndActivates_RefreshesBothFocusRules()
    {
        using var endpoint = new HyprlandConnectionTests.CompositorEndpoint();
        var state = new HyprlandWindowState(DispatchProxy.Create<IWindowImpl, NativeWindowStub>(), false, 1, _ => { }, _ => { })
        {
            IsFloating = true,
            CanActivate = false,
            IsHitTestVisible = false,
        };
        try
        {
            var serving = endpoint.ServeAsync(ReplyAsync);
            state.BeforeShow(new Size(300, 60));
            await serving;
            state.IsHitTestVisible = true;
            serving = endpoint.ServeAsync(ReplyAsync);
            state.RefreshInputPolicy();
            await serving;
            Assert.That(endpoint.Request, Does.Contain("no_focus=false,no_initial_focus=true"));

            state.CanActivate = true;
            serving = endpoint.ServeAsync(ReplyAsync);
            state.RefreshInputPolicy();
            await serving;
            Assert.That(endpoint.Request, Does.Contain("no_focus=false,no_initial_focus=false"));
        }
        finally
        {
            var cleanup = endpoint.ServeAsync(ReplyAsync);
            state.Dispose();
            await cleanup;
        }
    }

    [Test]
    public void OverlayBoundsChange_Placement_PreservesIndependentKeyboardAndPointerPolicy()
    {
        using var endpoint = new HyprlandConnectionTests.CompositorEndpoint();
        using var state = new HyprlandWindowState(DispatchProxy.Create<IWindowImpl, NativeWindowStub>(), false, 1, _ => { }, _ => { })
        {
            CanActivate = false,
            IsHitTestVisible = true,
        };
        state.SetOverlayBounds(new PixelRect(100, 200, 400, 100));
        Assert.That(state.CanActivate, Is.False);
        Assert.That(state.IsHitTestVisible, Is.True);
        Assert.That(state.Position, Is.EqualTo(new PixelPoint(100, 200)));
    }

    [Test]
    public async Task RemappedWindow_ObservesPreviousSurface_KeepsNewMapIdentity()
    {
        using var endpoint = new HyprlandConnectionTests.CompositorEndpoint();
        var native = DispatchProxy.Create<IWindowImpl, NativeWindowStub>();
        var stub = (NativeWindowStub)native;
        var state = new HyprlandWindowState(native, false, 1, _ => { }, _ => { })
        {
            IsFloating = true,
        };
        try
        {
            state.SetTitle("Everywhere");
            var serving = endpoint.ServeAsync(ReplyAsync);
            state.BeforeShow(new Size(300, 60));
            await serving;
            var previousToken = stub.LastTitle;
            state.Hidden();

            serving = endpoint.ServeAsync(ReplyAsync);
            state.BeforeShow(new Size(300, 60));
            await serving;
            var currentToken = stub.LastTitle;
            state.Observe(
            [
                new HyprlandWindow
                {
                    ProcessId = Environment.ProcessId,
                    InitialTitle = previousToken ?? "",
                    IsMapped = true,
                },
            ]);
            Assert.Multiple(() =>
            {
                Assert.That(currentToken, Is.Not.EqualTo(previousToken));
                Assert.That(stub.LastTitle, Is.EqualTo(currentToken));
                Assert.That(state.Address, Is.Null);
            });
        }
        finally
        {
            var cleanup = endpoint.ServeAsync(ReplyAsync);
            state.Dispose();
            await cleanup;
        }
    }

    private static async Task ReplyAsync(Socket socket, CancellationToken token)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes("ok"), SocketFlags.None, token);
    }

    /// <summary>
    /// Supplies an unmapped backend without opening a real desktop or display connection.
    /// </summary>
    public class NativeWindowStub : DispatchProxy
    {
        /// <summary>
        /// Gets the most recent title assigned to the native window.
        /// </summary>
        public string? LastTitle { get; private set; }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "SetTitle")
            {
                LastTitle = args?[0] as string;
            }
            return null;
        }
    }
}