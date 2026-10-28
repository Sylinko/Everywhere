using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Tests;

[Platform("Linux")]
[NonParallelizable]
public sealed class LinuxDesktopSessionIdentityTests
{
    [TestCase("GNOME", "wayland", "wayland-1", "parent-hyprland", "wayland:/run/user/1000/wayland-1")]
    [TestCase("niri", "wayland", "wayland-2", "parent-hyprland", "wayland:/run/user/1000/wayland-2")]
    [TestCase("KDE", "x11", "parent-wayland", "parent-hyprland", "x11::0")]
    [TestCase("Hyprland", "wayland", "wayland-1", "owned-hyprland", "hyprland:owned-hyprland")]
    public void ExplicitDesktop_GetIdentity_UsesTheSelectedDisplay(
        string desktop, string sessionType, string waylandDisplay, string compositor, string expected)
    {
        using var environment = new SessionEnvironment();
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", desktop);
        Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", sessionType);
        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", waylandDisplay);
        Environment.SetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE", compositor);
        Assert.That(RpcRuntimeIdentity.GetDesktopSessionId(), Is.EqualTo(expected));
    }

    [Test]
    public void WaylandSocket_AbsoluteAndRelativeNames_ReuseTheSameActivationEndpoint()
    {
        using var environment = new SessionEnvironment();
        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "wayland-1");
        var relative = ProcessRoleNames.GetApplicationActivationEndpoint(RpcRuntimeIdentity.GetDesktopSessionId());
        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "/run/user/1000/wayland-1");
        var absolute = ProcessRoleNames.GetApplicationActivationEndpoint(RpcRuntimeIdentity.GetDesktopSessionId());
        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "wayland-2");
        var nested = ProcessRoleNames.GetApplicationActivationEndpoint(RpcRuntimeIdentity.GetDesktopSessionId());
        Assert.Multiple(() =>
        {
            Assert.That(absolute, Is.EqualTo(relative));
            Assert.That(nested, Is.Not.EqualTo(relative));
        });
    }

    private sealed class SessionEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _previous;

        public SessionEnvironment()
        {
            var names = new[]
            {
                "XDG_CURRENT_DESKTOP", "XDG_SESSION_TYPE", "XDG_RUNTIME_DIR",
                "WAYLAND_DISPLAY", "DISPLAY", "HYPRLAND_INSTANCE_SIGNATURE",
            };
            _previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "niri");
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "wayland");
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", "/run/user/1000");
            Environment.SetEnvironmentVariable("DISPLAY", ":0");
            Environment.SetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE", null);
        }

        public void Dispose()
        {
            foreach (var pair in _previous)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }
}