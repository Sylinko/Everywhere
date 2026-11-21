using Avalonia;
using Everywhere.Chat.Plugins;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.Initialization;
using Everywhere.Interop;
using Everywhere.Linux.Automation;
using Everywhere.Linux.Chat.Plugins;
using Everywhere.Linux.Common;
using Everywhere.Linux.Desktop;
using Everywhere.Linux.Interop;
using Everywhere.Linux.Platform;
using Everywhere.Linux.ProcessIsolation;
using Everywhere.ProcessIsolation.Activation;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.ProcessIsolation.Watchdog;
using Everywhere.StrategyEngine;
using Everywhere.TextSelection;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Everywhere.Linux;

/// <summary>
/// Starts the Linux application and its isolated process roles.
/// </summary>
public static class Program
{
    /// <summary>
    /// Runs the requested process role and sets its exit code.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--linux-update-download", var address, var destination, var maximumBytes])
        {
            Environment.ExitCode = LinuxUpdateDownload.RunAsync(address, destination, maximumBytes).GetAwaiter().GetResult();
            return;
        }

        if (args is ["update", .. var updateArguments])
        {
            Environment.ExitCode = LinuxUpdateCommand.RunAsync(updateArguments).GetAwaiter().GetResult();
            return;
        }

        NativeMessageBox.Register(LinuxNativeMessageBox.Show);
        NativeSecretStore.Initialize(new LinuxSecretStore());
        Environment.ExitCode = RunAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--hyprland-shortcut", var pipeName, var registration])
        {
            await HyprlandInputHostSession.RelayAsync(pipeName, registration).ConfigureAwait(false);
            return 0;
        }

        if (args is ["--linux-primary-selection", var selectionPipe])
        {
            try
            {
                await PrimarySelectionMonitor.RelayAsync(selectionPipe, LinuxDesktopServices.Create().Compositor).ConfigureAwait(false);
                return 0;
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                Log.Debug("The PRIMARY relay was discarded ({FailureType}).", exception.GetType().Name);
                return 1;
            }
        }

        var peerVerifier = new LinuxNamedPipePeerVerifier();
        if (ProcessRoleCommandLine.ParseHostsControl(args) is { } hostsControlCommand)
        {
            return await HostsControlRunner.RunAsync(hostsControlCommand, DirectHostsControlPlatform.Instance, peerVerifier).ConfigureAwait(false);
        }

        var role = ProcessRoleCommandLine.Parse(args);
        if (role == ProcessRole.Input)
        {
            return await ProcessRoleHostRunner.RunAsync(role, args, peerVerifier, LinuxDesktopServices.CreateInputHostSession).ConfigureAwait(false);
        }

        if (role == ProcessRole.Automation)
        {
            return await ProcessRoleHostRunner.RunAsync(
                    role,
                    args,
                    peerVerifier,
                    static () => LinuxDesktopServices.Create().CreateAutomationHostSession())
                .ConfigureAwait(false);
        }

        // Claim before runtime initialization; Entrance disposes services and the lock before running post-exit hooks.
        var activationServer = ApplicationActivationEntrance.TryCreateServer(args, peerVerifier);
        if (activationServer is null)
        {
            return await ApplicationActivationEntrance.SendToPrimaryAsync(args, peerVerifier).ConfigureAwait(false);
        }

        await using (Entrance.Initialize(activationServer))
        {
            return await RunMainAsync(args, activationServer).ConfigureAwait(false);
        }
    }

    private static Task<int> RunMainAsync(string[] args, ApplicationActivationServer activationServer)
    {
        var serviceProvider = ServiceLocator.Build(x => LinuxDesktopServices.AddServices(x)

                #region Basic

                .AddApplicationLogging()
                .AddSingleton(activationServer)
                .AddSingleton<INamedPipePeerVerifier, LinuxNamedPipePeerVerifier>()
                .AddProcessIsolation()
                .AddSingleton<LinuxWindowHelper>()
                .AddSingleton<IWindowHelper>(sp => sp.GetRequiredService<LinuxWindowHelper>())
                .AddSingleton(sp =>
                    sp.GetRequiredService<LinuxDesktopServices>().CreateScreenSelectionService(sp.GetRequiredService<INativeHelper>()))
                .AddSingleton<INativeHelper, LinuxNativeHelper>()
                .AddSingleton<ILinkFileOperations, LinkFileOperations>()
                .AddSingleton(sp => sp.GetRequiredService<LinuxDesktopServices>().CreateOverlayInputWatcher())
                .AddSingleton<IPlatformUpdateHandler, LinuxUpdateHandler>()
                .AddSingleton<ISoftwareUpdater, SoftwareUpdater>()
                .AddSettings()
                .AddWatchdogManager()
                .ConfigureNetwork()
                .AddViewsAndViewModels()
                .AddDatabaseAndStorage()
                .AddCloudClient()
                .AddChatEssentials()
                .AddTextSelection()

                #endregion

                #region Chat Plugins

                .AddTransient<BuiltInChatPlugin, FdFindPlugin>()

                #endregion

                #region Strategy Engine

                .AddStrategyEngine()

                #endregion

                #region Initialize

                .AddAsyncInitializer<ChatWindowInitializer>()
                .AddAsyncInitializer<SoftwareUpdaterInitializer>()

            #endregion

        );

        return BuildAvaloniaApp(serviceProvider).RunClassicDesktopAsync(args);
    }

    private static AppBuilder BuildAvaloniaApp(IServiceProvider serviceProvider)
    {
        var desktop = serviceProvider.GetRequiredService<LinuxDesktopServices>();
        var builder = App.CreateBuilder(serviceProvider).UsePlatformDetect();
        if (desktop.Kind != LinuxDesktopKind.X11)
        {
            builder = builder.UseWaylandWithFallback();
        }

        if (desktop.IsStandardWayland)
        {
            // Avalonia 12.1 recreates hidden Wayland surfaces without restoring
            // the no-decoration request. Keep client decorations consistent
            // across Hide/Show on desktops that offer server decorations.
#pragma warning disable AVALONIA_WAYLAND_FORCE_CSD
            builder = builder.With(new WaylandPlatformOptions { ForceDrawnDecorations = true });
#pragma warning restore AVALONIA_WAYLAND_FORCE_CSD
        }

        if (desktop.Kind != LinuxDesktopKind.Hyprland)
        {
            builder = builder.AfterSetup(_ => StandardDesktopWindowAppearance.InstallStyles());
        }

        return builder.AfterPlatformServicesSetup(_ =>
        {
            BetterBclLauncher.Shared = new LinuxDesktopLauncher(BetterBclLauncher.Shared);
            serviceProvider.GetRequiredService<LinuxDesktopServices>().InstallWindowingPlatform();
        });
    }
}
