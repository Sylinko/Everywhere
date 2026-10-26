using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Everywhere.Linux.Desktop;
using Everywhere.Linux.Hyprland;
using Everywhere.Linux.Platform;

namespace Everywhere.Linux.Tests;

[NonParallelizable]
public sealed class HyprlandGeometryTests
{
    [Test]
    public void RoundedLogicalOutput_CapturePixelBounds_KeepsNativeDensityAndIntegerPixels()
    {
        var pixels = LinuxScreenSelectionService.CapturePixelBounds(
            new PixelRect(40, 450, 600, 84),
            new PixelRect(0, 0, 2194, 1234),
            1.75,
            new PixelSize(3839, 2159));
        Assert.That(pixels, Is.EqualTo(new PixelRect(70, 787, 1050, 147)));
    }

    [Test]
    public void MirroredAndDisabledOutputs_VisibleOutputs_DoNotCreateDuplicateDesktopSpaces()
    {
        var source = new HyprlandMonitor
        {
            Name = "HDMI-A-1",
            Width = 3840,
            Height = 2160,
            Scale = 1.75,
        };
        var mirror = new HyprlandMonitor
        {
            Name = "eDP-1",
            MirrorOf = "1",
            Width = 2560,
            Height = 1600,
        };
        var disabled = new HyprlandMonitor
        {
            Name = "DP-1",
            IsDisabled = true,
        };
        Assert.That(HyprlandCompositorStateProvider.VisibleOutputs([mirror, disabled, source]), Is.EqualTo(new[] { source }));
    }

    [Test]
    public void ReverseDragAcrossNegativeOrigin_SelectionRectangle_PreservesTheEntireRegion()
    {
        Assert.That(LinuxScreenSelectionService.SelectionRectangle(new PixelPoint(300, 200), new PixelPoint(-100, -50)),
            Is.EqualTo(new PixelRect(-100, -50, 400, 250)));
    }

    [Test]
    public void FractionalScaleWithNegativeOutputOrigin_RoundTrip_PreservesWindowBounds()
    {
        var map = new OutputCoordinateMap(new PixelRect(-3840, 0, 3840, 2160), new PixelRect(-2194, 0, 2194, 1234));
        var native = new PixelRect(-2100, 120, 900, 600);
        var pixels = map.ToAvalonia(native);
        var first = map.ToLogical(pixels.Position);
        var last = map.ToLogical(new PixelPoint(pixels.Right, pixels.Bottom));
        Assert.That(Math.Abs(first.X - native.X), Is.LessThanOrEqualTo(1));
        Assert.That(Math.Abs(first.Y - native.Y), Is.LessThanOrEqualTo(1));
        Assert.That(Math.Abs(last.X - native.Right), Is.LessThanOrEqualTo(1));
        Assert.That(Math.Abs(last.Y - native.Bottom), Is.LessThanOrEqualTo(1));
    }

    [Test]
    public void RotatedOutput_LogicalBounds_ExchangesPhysicalAxes()
    {
        var monitor = new HyprlandMonitor
        {
            X = -1440,
            Y = -100,
            Width = 3840,
            Height = 2160,
            Scale = 1.5,
            Transform = 1,
        };
        Assert.That(monitor.Bounds, Is.EqualTo(new PixelRect(-1440, -100, 1440, 2560)));
    }

    [Test]
    public async Task OverlayAndInactiveWorkspace_WindowAt_ChoosesApplicationBelowOverlay()
    {
        var monitor = new HyprlandMonitor
        {
            Id = 2,
            Width = 1920,
            Height = 1080,
            ActiveWorkspace = new()
            {
                Id = 1,
            },
        };
        var target = Window("0x10", 100, 1, 3);
        var overlay = Window("0x20", 200, 1, 0);
        var inactive = Window("0x30", 300, 9, 0);
        var desktop = await ReadDesktopAsync(monitor, [inactive, overlay, target]);
        Assert.That(desktop.WindowAt(new PixelPoint(200, 200), 200), Has.Property("Id").EqualTo(target.Address));
    }

    [Test]
    public async Task VisibleSpecialWorkspace_WindowAt_TakesPriorityOverOrdinaryFloatingWindow()
    {
        var monitor = new HyprlandMonitor
        {
            Id = 2,
            Width = 1920,
            Height = 1080,
            ActiveWorkspace = new()
            {
                Id = 1,
            },
            SpecialWorkspace = new()
            {
                Id = -98,
            },
        };
        var normal = Window("0x10", 100, 1, 0) with
        {
            IsFloating = true,
        };
        var special = Window("0x20", 200, -98, 4);
        var desktop = await ReadDesktopAsync(monitor, [normal, special]);
        Assert.That(desktop.WindowAt(new PixelPoint(200, 200))?.Id, Is.EqualTo(special.Address));
    }

    [Test]
    public async Task HiddenAndUnmappedWindows_WindowAt_AreNeverSelected()
    {
        var monitor = new HyprlandMonitor
        {
            Id = 2,
            Width = 1920,
            Height = 1080,
            ActiveWorkspace = new()
            {
                Id = 1,
            },
        };
        var desktop = await ReadDesktopAsync(monitor,
        [
            Window("0x10", 100, 1, 0) with
            {
                IsHidden = true,
            },
            Window("0x20", 200, 1, 0) with
            {
                IsMapped = false,
            },
        ]);
        Assert.That(desktop.WindowAt(new PixelPoint(200, 200)), Is.Null);
    }

    [Test]
    public void MixedDensityOutputs_LocalCoordinates_DoNotScaleTheGlobalOrigin()
    {
        var left = new OutputCoordinateMap(new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080));
        var right = new OutputCoordinateMap(new PixelRect(1920, -100, 3840, 2160), new PixelRect(1920, -100, 2194, 1234));
        Assert.That(left.ToLogical(new PixelPoint(960, 540)), Is.EqualTo(new PixelPoint(960, 540)));
        Assert.That(right.ToLogical(new PixelPoint(1920, -100)), Is.EqualTo(new PixelPoint(1920, -100)));
        Assert.That(right.ToLogical(new PixelPoint(3840, 980)), Is.EqualTo(new PixelPoint(3017, 517)));
    }

    private static async Task<DesktopSnapshot> ReadDesktopAsync(HyprlandMonitor monitor, HyprlandWindow[] windows)
    {
        using var endpoint = new HyprlandConnectionTests.CompositorEndpoint();
        var serving = ServeAsync();
        var provider = new HyprlandCompositorStateProvider(new HyprlandConnection());
        var observed = await provider.GetWindowsInStackingOrderAsync();
        await serving;
        Assert.That(provider.StackingOrderKind, Is.EqualTo(WindowStackingOrderKind.FocusHistoryEstimate));
        Assert.That(observed.All(window => window.FrameRect is null), Is.True);
        return new DesktopSnapshot([new OutputInfo(monitor.Name, monitor.Bounds, monitor.Scale)], observed);

        async Task ServeAsync()
        {
            await endpoint.ServeAsync(async (socket, token) =>
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new[] { monitor }), SocketFlags.None, token));
            await endpoint.ServeAsync(async (socket, token) =>
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(windows), SocketFlags.None, token));
        }
    }

    private static HyprlandWindow Window(string address, int pid, int workspace, int focus)
    {
        return new()
        {
            Address = address,
            ProcessId = pid,
            Position = [100, 100],
            Size = [800, 600],
            IsMapped = true,
            MonitorId = 2,
            Workspace = new()
            {
                Id = workspace,
            },
            FocusHistoryId = focus,
        };
    }
}