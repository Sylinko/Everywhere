using Avalonia;
using Avalonia.Platform;
using Everywhere.Extensions;
using Everywhere.Interop;
using Everywhere.Linux.Automation;
using Everywhere.Linux.Hyprland;
using Everywhere.Linux.Platform;
using Everywhere.Linux.Wayland;
using Everywhere.Linux.X11;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace Everywhere.Linux.Desktop;

/// <summary>
/// Selects and composes one Linux desktop backend per process. Common automation and
/// selection code never selects a compositor implicitly. New desktops provide these
/// services here, together with their native windowing and input integrations.
/// </summary>
public sealed class LinuxDesktopServices
{
    /// <summary>
    /// Gets the explicitly selected desktop integration.
    /// </summary>
    public LinuxDesktopKind Kind { get; }

    /// <summary>
    /// Gets the selected compositor state provider.
    /// </summary>
    public ICompositorStateProvider Compositor { get; }

    /// <summary>
    /// Gets the selected controller for process-owned windows.
    /// </summary>
    public IOverlayWindowController Overlays { get; }

    /// <summary>
    /// Gets the selected output capture implementation.
    /// </summary>
    public ISilentScreenCapturer ScreenCapturer { get; }

    private LinuxDesktopServices(
        LinuxDesktopKind kind,
        ICompositorStateProvider compositor,
        IOverlayWindowController overlays,
        ISilentScreenCapturer screenCapturer)
    {
        Kind = kind;
        Compositor = compositor;
        Overlays = overlays;
        ScreenCapturer = screenCapturer;
    }

    /// <summary>
    /// Registers Main's desktop services, shared focused shortcut recording and the available global backend.
    /// </summary>
    public static IServiceCollection AddServices(IServiceCollection services)
    {
        var desktop = Create();
        services.AddSingleton(desktop)
            .AddSingleton(sp => sp.GetRequiredService<LinuxDesktopServices>().Compositor)
            .AddSingleton(sp => sp.GetRequiredService<LinuxDesktopServices>().Overlays)
            .AddSingleton(sp => sp.GetRequiredService<LinuxDesktopServices>().ScreenCapturer);
        if (desktop.Kind == LinuxDesktopKind.Hyprland)
        {
            services.AddInputHostShortcutListener();
            services.Replace(ServiceDescriptor.Singleton<IShortcutListener>(sp =>
                new LinuxShortcutListener(sp.GetRequiredService<InputHostShortcutListener>())));
        }
        else
        {
            services.AddSingleton<IShortcutListener>(new LinuxShortcutListener(null));
        }

        return services;
    }

    /// <summary>
    /// Selects the supported backend for the explicitly inherited session.
    /// </summary>
    public static LinuxDesktopServices Create()
    {
        var kind = DetectSession();
        if (kind is LinuxDesktopKind.PlasmaWayland or LinuxDesktopKind.GnomeWayland)
        {
            return CreateStandardWayland(kind);
        }

        if (kind == LinuxDesktopKind.X11)
        {
            return new(LinuxDesktopKind.X11, new X11CompositorStateProvider(), new X11WindowController(), new X11SilentScreenCapturer());
        }

        var compositor = new HyprlandCompositorStateProvider(new HyprlandConnection());
        return new(LinuxDesktopKind.Hyprland, compositor, new HyprlandOverlayWindowController(), new HyprlandSilentScreenCapturer(compositor));
    }

    private static LinuxDesktopServices CreateStandardWayland(LinuxDesktopKind kind)
    {
        return new(kind, new UnavailableWaylandStateProvider(), new StandardWaylandWindowController(), new UnavailableWaylandScreenCapturer());
    }

    /// <summary>
    /// Creates a picker only when its global state, placement and capture services exist.
    /// </summary>
    public IScreenSelectionService CreateScreenSelectionService(INativeHelper nativeHelper)
    {
        return Kind is LinuxDesktopKind.Hyprland or LinuxDesktopKind.X11
            ? new LinuxScreenSelectionService(Compositor, Overlays, ScreenCapturer, nativeHelper)
            : new UnavailableWaylandSelectionService();
    }

    /// <summary>
    /// Installs the native UI bridge before creating any Avalonia windows.
    /// </summary>
    public void InstallWindowingPlatform()
    {
        if (Kind == LinuxDesktopKind.Hyprland)
        {
            HyprlandWindowingPlatform.Install();
        }
        else
        {
            Log.Information(
                "Initialized {DesktopKind} UI over {WindowingBackend}.",
                Kind,
                AvaloniaLocator.Current.GetRequiredService<IWindowingPlatform>().GetType().FullName);
        }
    }

    /// <summary>
    /// Creates the compositor-specific global input observer behind its shared contract.
    /// </summary>
    public IOverlayInputWatcher CreateOverlayInputWatcher()
    {
        return Kind == LinuxDesktopKind.Hyprland ? new HyprlandOverlayInputWatcher() : new UnsupportedOverlayInputWatcher();
    }

    /// <summary>
    /// Creates the compositor-specific shortcut Host behind its shared contract.
    /// </summary>
    public static IProcessRoleSession CreateInputHostSession()
    {
        return DetectSession() == LinuxDesktopKind.Hyprland ? new HyprlandInputHostSession() : new DesktopInputHostSession();
    }

    /// <summary>
    /// Constructs only the automation capabilities implemented by the selected desktop.
    /// </summary>
    public IProcessRoleSession CreateAutomationHostSession()
    {
        var backend = new LinuxVisualElementBackend(Compositor, ScreenCapturer);
        return Kind == LinuxDesktopKind.Hyprland
            ? new AutomationHostSession(backend, backend, new LinuxTextSelectionMonitorFactory(backend))
            : new AutomationHostSession(backend, backend, new UnavailableTextSelectionMonitorFactory());
    }

    /// <summary>
    /// Reads the explicit display protocol before considering a compositor-specific inherited marker.
    /// </summary>
    public static LinuxDesktopKind DetectSession()
    {
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        var waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (sessionType == "x11" && !string.IsNullOrEmpty(display))
        {
            return LinuxDesktopKind.X11;
        }

        var desktopNames = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!string.IsNullOrEmpty(waylandDisplay))
        {
            if (desktopNames.Contains("KDE", StringComparer.OrdinalIgnoreCase) || desktopNames.Contains("Plasma", StringComparer.OrdinalIgnoreCase))
            {
                return LinuxDesktopKind.PlasmaWayland;
            }

            if (desktopNames.Contains("GNOME", StringComparer.OrdinalIgnoreCase))
            {
                return LinuxDesktopKind.GnomeWayland;
            }

            if (desktopNames.Length > 0 && !desktopNames.Contains("Hyprland", StringComparer.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("The selected Wayland desktop integration is not implemented.");
            }
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")))
        {
            return LinuxDesktopKind.Hyprland;
        }

        if (string.IsNullOrEmpty(waylandDisplay) && !string.IsNullOrEmpty(display))
        {
            return LinuxDesktopKind.X11;
        }

        throw new NotSupportedException("The selected Linux desktop integration is not implemented.");
    }
}