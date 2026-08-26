using Avalonia;
using Avalonia.Controls;
using Everywhere.Automation;
using Everywhere.Chat.Plugins;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.Initialization;
using Everywhere.Interop;
using Everywhere.Mac.Chat.Plugin;
using Everywhere.Mac.Common;
using Everywhere.Mac.Automation;
using Everywhere.Mac.Interop;
using Everywhere.Mac.ProcessIsolation.Input;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Watchdog;
using Everywhere.StrategyEngine;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Mac;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        NativeMessageBox.Register(NSAlertMessageBox.Show);
        Environment.ExitCode = RunAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (ProcessRoleCommandLine.ParseHostsControl(args) is { } hostsControlOperation)
        {
            return await HostsControlRunner.RunAsync(hostsControlOperation).ConfigureAwait(false);
        }

        var role = ProcessRoleCommandLine.Parse(args);
        if (role is not ProcessRole.Main)
        {
            return await (role is ProcessRole.Input ?
                ProcessRoleHostRunner.RunAsync(role, args, static () => new MacInputHostSession()) :
                ProcessRoleHostRunner.RunAsync(role, args)).ConfigureAwait(false);
        }

        await using var entrance = Entrance.Initialize(args);
        if (!entrance.IsPrimary)
        {
            return await entrance.ForwardAsync().ConfigureAwait(false);
        }

        return await RunMainAsync(args).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the full Avalonia/Core startup state machine out of early Host and
    /// controller dispatch so those paths do not resolve the production graph.
    /// </summary>
    private static async Task<int> RunMainAsync(string[] args)
    {
        if (!NSThread.IsMain)
        {
            throw new InvalidOperationException("Avalonia must be initialized on the macOS main thread.");
        }

        NSApplication.CheckForIllegalCrossThreadCalls = false;
        NSApplication.Init();
        PermissionHelper.EnsureAccessibilityTrusted();
        CGDisplayTopology.Initialize();
        NSApplication.SharedApplication.Delegate = new AppDelegate();

        await using var serviceProvider = ServiceLocator.Build(x => x

            #region Basic

                .AddApplicationLogging()
                .AddProcessIsolation()
                .AddInputHostShortcutListener()
                .AddSingleton<MacVisualElementBackend>()
                .AddSingleton<IVisualElementBackend>(sp => sp.GetRequiredService<MacVisualElementBackend>())
                .AddSingleton<MacScreenSelectionService>()
                .AddSingleton<IScreenSelectionService>(sp => sp.GetRequiredService<MacScreenSelectionService>())
                .AddSingleton<MacTextSelectionWatcher>()
                .AddSingleton<ITextSelectionWatcher>(sp => sp.GetRequiredService<MacTextSelectionWatcher>())
                .AddSingleton<INativeHelper, NativeHelper>()
                .AddSingleton<IWindowHelper, WindowHelper>()
                .AddSingleton<IPlatformUpdateHandler, MacUpdateHandler>()
                .AddSingleton<ISoftwareUpdater, SoftwareUpdater>()
                .AddSettings()
                .AddWatchdogManager()
                .ConfigureNetwork()
                .AddViewsAndViewModels()
                .AddDatabaseAndStorage()
                .AddCloudClient()
                .AddChatEssentials()

            #endregion

            #region Chat Plugins

            .AddTransient<BuiltInChatPlugin, SystemPlugin>()

            #endregion

            #region Strategy Engine

            .AddStrategyEngine()

            #endregion

            #region Initialize

            .AddTransient<IAsyncInitializer, ChatWindowInitializer>()
            .AddTransient<IAsyncInitializer, UpdaterInitializer>()

            #endregion

        );

        var exitCode = BuildAvaloniaApp(serviceProvider).StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        if (Application.Current is App app)
        {
            await app.InitializationTask.ConfigureAwait(false);
        }

        return exitCode;
    }

    private static AppBuilder BuildAvaloniaApp(IServiceProvider serviceProvider) =>
        AppBuilder.Configure(() => new App(serviceProvider))
            .UsePlatformDetect()
            .With(
                new AvaloniaNativePlatformOptions
                {
                    AppSandboxEnabled = false
                })
            .With(
                new MacOSPlatformOptions
                {
                    // These settings are important for showing chat window over other fullscreen apps
                    ShowInDock = false,
                    DisableAvaloniaAppDelegate = true
                })
            .WithInterFont()
            .LogToTrace();
}
