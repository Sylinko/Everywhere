using Everywhere.Linux.Desktop;
using Everywhere.ProcessIsolation.Hosts.Input;

namespace Everywhere.Linux.Tests;

[NonParallelizable]
public sealed class LinuxSessionTests
{
    [Test]
    public void ExplicitX11Session_WithInheritedWaylandMarkers_SelectsX11()
    {
        var names = new[] { "XDG_SESSION_TYPE", "DISPLAY", "WAYLAND_DISPLAY", "HYPRLAND_INSTANCE_SIGNATURE" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "x11");
            Environment.SetEnvironmentVariable("DISPLAY", ":99");
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "inherited-parent");
            Environment.SetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE", "inherited-parent");
            Assert.That(LinuxDesktopServices.DetectSession(), Is.EqualTo(LinuxDesktopKind.X11));
        }
        finally
        {
            foreach (var pair in previous)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }

    [TestCase("KDE", LinuxDesktopKind.PlasmaWayland)]
    [TestCase("Plasma", LinuxDesktopKind.PlasmaWayland)]
    [TestCase("test:KDE", LinuxDesktopKind.PlasmaWayland)]
    [TestCase("GNOME", LinuxDesktopKind.GnomeWayland)]
    [TestCase("ubuntu:GNOME", LinuxDesktopKind.GnomeWayland)]
    [TestCase("GNOME-Classic:GNOME", LinuxDesktopKind.GnomeWayland)]
    [TestCase("niri", LinuxDesktopKind.NiriWayland)]
    [TestCase("test:NIRI", LinuxDesktopKind.NiriWayland)]
    public void ExplicitWaylandDesktop_WithInheritedHyprlandMarker_LeavesGlobalCapabilitiesUnavailable(
        string currentDesktop,
        LinuxDesktopKind expectedKind)
    {
        var names = new[] { "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP", "DISPLAY", "WAYLAND_DISPLAY", "HYPRLAND_INSTANCE_SIGNATURE" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "wayland");
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", currentDesktop);
            Environment.SetEnvironmentVariable("DISPLAY", ":99");
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "test-wayland");
            Environment.SetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE", "inherited-parent");
            var desktop = LinuxDesktopServices.Create();
            Assert.That(desktop.Kind, Is.EqualTo(expectedKind));
            Assert.That(desktop.IsStandardWayland, Is.True);
            Assert.That(desktop.CreateOverlayInputWatcher().IsSupported, Is.False);
            Assert.That(desktop.Compositor.StackingOrderKind, Is.EqualTo(WindowStackingOrderKind.Unavailable));
            Assert.ThrowsAsync<NotSupportedException>(async () => await desktop.Compositor.GetOutputsAsync());
        }
        finally
        {
            foreach (var pair in previous)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }

    [Test]
    public async Task StarterInputHost_UnportedShortcutState_IsRejectedWithoutClaimingRegistration()
    {
        await using var host = new DesktopInputHostSession();
        var result = await host.ApplyStateAsync(new ApplyInputStateRequest
        {
            KeyboardRegistrations =
            [
                new()
                {
                    RegistrationId = 1,
                    Key = 1,
                    Modifiers = 0,
                },
            ],
            MouseRegistrations = [],
            CaptureId = 0,
        });
        Assert.That(result.IsApplied, Is.False);
        var empty = new ApplyInputStateRequest
        {
            KeyboardRegistrations = [],
            MouseRegistrations = [],
            CaptureId = 0,
        };
        Assert.That((await host.ApplyStateAsync(empty)).IsApplied, Is.True);
        await host.BeginDrainingAsync();
        Assert.That((await host.ApplyStateAsync(empty)).IsApplied, Is.False);
    }
}