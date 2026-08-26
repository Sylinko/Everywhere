using System.IO.Pipes;
using System.Runtime.CompilerServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia.Input;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Hosts.Diagnostics;
using Everywhere.ProcessIsolation.Hosts.Input;
using Everywhere.ProcessIsolation.Hosts.Lifecycle;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.Windows.Interop;
using Everywhere.Windows.ProcessIsolation.Input;

namespace Everywhere.Windows.Tests.ProcessIsolation.Input;

[NonParallelizable]
public sealed class WindowsInputHostSessionTests
{
    private static readonly IHostDiagnosticsRpc Diagnostics = new NullHostDiagnostics();

    [Test]
    public async Task RoleHostRunner_WithWindowsInputSession_AppliesStateAndDrainsNativeHooks()
    {
        var endpoint = $"Everywhere.Test.WindowsInputRole.{Guid.NewGuid():N}";
        using var runnerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runner = ProcessRoleHostRunner.RunAsync(
            ProcessRole.Input,
            new[] { "--rpc-endpoint", endpoint },
            static () => new WindowsInputHostSession(),
            runnerCancellation.Token);

        await using var stream = new NamedPipeClientStream(
            ".",
            endpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await stream.ConnectAsync(5000);
        await using var connection = new RpcConnection(stream, isServer: false);
        connection.Start();

        var identity = RpcRuntimeIdentity.CreateCurrent(ProcessRole.Main);
        var handshake = await connection.PerformHandshakeAsync(
            new RpcHandshake
            {
                AssemblyInformationalVersion = identity.AssemblyInformationalVersion,
                Role = identity.WireName,
                ProcessId = identity.ProcessId,
                DesktopSessionId = identity.DesktopSessionId
            });
        RpcHandshakeValidator.ValidateAcceptedPeer(handshake, ProcessRole.Input, identity);

        var stateResponse = await new InputHostRpcClient(connection).ApplyStateAsync(
            new ApplyInputStateRequest
            {
                KeyboardRegistrations = [],
                MouseRegistrations =
                [
                    new InputMouseRegistration
                    {
                        RegistrationId = 1,
                        Button = (int)MouseButton.Left,
                        DelayTicks = TimeSpan.FromMinutes(1).Ticks
                    }
                ],
                CaptureId = 0
            });
        var shutdown = await new HostLifecycleRpcClient(connection).ShutdownAsync(new ShutdownRequest());
        var exitCode = await runner.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(stateResponse.IsApplied, Is.True);
            Assert.That(shutdown.Accepted, Is.True);
            Assert.That(exitCode, Is.Zero);
        });
    }

    [Test]
    public async Task NativeEventQueue_WhenCapacityIsExhausted_FailsOpen()
    {
        await using var session = new WindowsInputHostSession();
        var inputEvent = new WindowsInputHook.InputEvent(
            WindowsInputHook.InputEventKind.ShortcutTriggered,
            1);

        for (var index = 0; index < 64; index++)
        {
            Assert.That(TryQueue(session, inputEvent), Is.True);
        }

        Assert.That(TryQueue(session, inputEvent), Is.False);
    }

    [Test]
    public void MouseButtonDown_WhenRegistrationExists_PublishesItsRegistrationId()
    {
        var published = new List<WindowsInputHook.InputEvent>();
        using var input = new WindowsInputHook(
            inputEvent =>
            {
                published.Add(inputEvent);
                return true;
            },
            Diagnostics);
        input.ApplyState(
            new ApplyInputStateRequest
            {
                KeyboardRegistrations = [],
                MouseRegistrations =
                [
                    new InputMouseRegistration
                    {
                        RegistrationId = 42,
                        Button = (int)MouseButton.Left,
                        DelayTicks = 0
                    }
                ],
                CaptureId = 0
            });

        var hookStruct = new MSLLHOOKSTRUCT();
        var blockNext = false;
        InvokeMouseHook(input, WINDOW_MESSAGE.WM_LBUTTONDOWN, ref hookStruct, ref blockNext);

        Assert.That(published, Has.Count.EqualTo(1));
        Assert.That(published[0].Kind, Is.EqualTo(WindowsInputHook.InputEventKind.ShortcutTriggered));
        Assert.That(published[0].Id, Is.EqualTo(42));
        Assert.That(blockNext, Is.False);
    }

    [Test]
    public async Task MessageHandler_WhenSubscriptionIsDisposed_DoesNotReceiveLaterMessages()
    {
        const uint MessageId = (uint)WINDOW_MESSAGE.WM_APP + 0x321;
        var received = 0;
        var firstMessage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (MessageWindow.Shared.AddHandler(
                   MessageId,
                   (in MSG _) =>
                   {
                       Interlocked.Increment(ref received);
                       firstMessage.TrySetResult();
                   }))
        {
            PInvoke.PostMessage(MessageWindow.Shared.HWnd, MessageId, 0, 0);
            await firstMessage.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        PInvoke.PostMessage(MessageWindow.Shared.HWnd, MessageId, 0, 0);
        await Task.Delay(100);

        Assert.That(received, Is.EqualTo(1));
    }

    [Test]
    public void LowLevelHook_WhenDisposed_WaitsForHookThreadExit()
    {
        var hook = LowLevelHook.CreateKeyboardHook(
            static (WINDOW_MESSAGE _, ref KBDLLHOOKSTRUCT _, ref bool _) => { });

        Assert.DoesNotThrow(hook.Dispose);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TryQueue")]
    private static extern bool TryQueue(
        WindowsInputHostSession session,
        WindowsInputHook.InputEvent inputEvent);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "MouseHookProc")]
    private static extern void InvokeMouseHook(
        WindowsInputHook input,
        WINDOW_MESSAGE message,
        ref MSLLHOOKSTRUCT hookStruct,
        ref bool blockNext);

    private sealed class NullHostDiagnostics : IHostDiagnosticsRpc
    {
        public ValueTask LogAsync(
            HostLogNotification notification,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
