using Avalonia;
using Avalonia.Controls;
using Everywhere.Chat.Plugins;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.Initialization;
using Everywhere.Interop;
using Everywhere.Mac.Automation;
using Everywhere.Mac.Chat.Plugin;
using Everywhere.Mac.Common;
using Everywhere.Mac.Interop;
using Everywhere.Mac.ProcessIsolation;
using Everywhere.Mac.ProcessIsolation.Input;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.ProcessIsolation.Watchdog;
using Everywhere.StrategyEngine;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Mac;

public static class Program
{
    private const string ApplicationInstanceName = "com.sylinko.everywhere";

    [STAThread]
    public static int Main(string[] args)
    {
        NativeMessageBox.Register(NSAlertMessageBox.Show);
        return RunAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var peerVerifier = MacNamedPipePeerVerifier.Instance;
        if (ProcessRoleCommandLine.ParseHostsControl(args) is { } hostsControlCommand)
        {
            return await HostsControlRunner.RunAsync(hostsControlCommand, DirectHostsControlPlatform.Instance, peerVerifier).ConfigureAwait(false);
        }

        var role = ProcessRoleCommandLine.Parse(args);
        switch (role)
        {
            case ProcessRole.Main:
            {
                return await RunApplicationAsync(args, peerVerifier).ConfigureAwait(false);
            }
            case ProcessRole.Input:
            {
                return await ProcessRoleHostRunner
                    .RunAsync(role, args, peerVerifier, static () => new MacInputHostSession())
                    .ConfigureAwait(false);
            }
            case ProcessRole.Automation:
            {
                return await RunAutomationHostAsync(args, peerVerifier).ConfigureAwait(false);
            }
            default:
            {
                throw new ArgumentOutOfRangeException(nameof(role), role, null);
            }
        }
    }

    private static async Task<int> RunApplicationAsync(string[] args, INamedPipePeerVerifier peerVerifier)
    {
        // Retain the previous named-object claim for direct executable and LaunchAgent starts.
        // Do not acquire the mutex: the claim must outlive UI and asynchronous DI cleanup
        // without being tied to the creating thread. Native application events handle activation.
        using var instanceClaim = new Mutex(false, ApplicationInstanceName, out var isPrimary);
        if (!isPrimary)
        {
            // Bundle activation is delivered by native events; direct secondary starts simply exit.
            Console.WriteLine("Everywhere is already running.");
            return 0;
        }

        Entrance.Initialize();
        return await RunMainAsync(args, peerVerifier).ConfigureAwait(false);
    }

    private static Task<int> RunAutomationHostAsync(string[] args, INamedPipePeerVerifier peerVerifier)
    {
        if (!NSThread.IsMain)
        {
            throw new InvalidOperationException("The macOS Automation Host must initialize AppKit on the process main thread.");
        }

        NSApplication.Init();
        var application = NSApplication.SharedApplication;
        application.ActivationPolicy = NSApplicationActivationPolicy.Prohibited;
        CGDisplayTopology.Initialize();

        // ProcessRoleHostRunner creates the platform session synchronously before
        // its first connection wait, so AppKit and AX bootstrap remain on the
        // process main thread. RPC work continues independently while this thread
        // owns the native application loop.
        var hostTask = ProcessRoleHostRunner.RunAsync(
            ProcessRole.Automation,
            args,
            peerVerifier,
            static () => new AutomationHostSession(
                new MacVisualElementBackend(),
                new MacVisualPickerResolver(),
                new MacTextSelectionMonitorFactory()));
        if (hostTask.IsCompleted)
        {
            return hostTask;
        }

        StopApplicationWhenHostExitsAsync(hostTask, application).Detach(NativeMessageBox.ExceptionHandler);
        application.Run();
        return hostTask;
    }

    private static async Task StopApplicationWhenHostExitsAsync(Task hostTask, NSApplication application)
    {
        try
        {
            await hostTask.ConfigureAwait(false);
        }
        finally
        {
            application.BeginInvokeOnMainThread(() =>
            {
                application.Stop(application);
                using var wakeEvent = NSEvent.OtherEvent(NSEventType.ApplicationDefined, CGPoint.Empty, 0, 0, 0, null, 0, 0, 0);
                application.PostEvent(wakeEvent, true);
            });
        }
    }

    /// <summary>
    /// Keeps the full Avalonia/Core startup state machine out of early Host and
    /// controller dispatch so those paths do not resolve the production graph.
    /// </summary>
    private static async Task<int> RunMainAsync(string[] args, INamedPipePeerVerifier peerVerifier)
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

        var serviceProvider = ServiceLocator.Build(x => x

                #region Basic

                .AddApplicationLogging()
                .AddSingleton(peerVerifier)
                .AddProcessIsolation()
                .AddInputHostShortcutListener()
                .AddSingleton<MacScreenSelectionService>()
                .AddSingleton<IScreenSelectionService>(sp => sp.GetRequiredService<MacScreenSelectionService>())
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
                .AddStrategyEngine()

                #endregion

                #region Chat Plugins

                .AddTransient<BuiltInChatPlugin, SystemPlugin>()

                #endregion

                #region Initialize

                .AddTransient<IAsyncInitializer, ChatWindowInitializer>()
                .AddTransient<IAsyncInitializer, UpdaterInitializer>()

            #endregion

        );

        try
        {
            var exitCode = BuildAvaloniaApp(serviceProvider).StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
            if (Application.Current is App app)
            {
                await app.WaitForShutdownAsync().ConfigureAwait(false);
            }

            return exitCode;
        }
        finally
        {
            // Avalonia's UI synchronization context no longer pumps after the desktop lifetime exits.
            await serviceProvider.DisposeAsync().ConfigureAwait(false);
        }
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