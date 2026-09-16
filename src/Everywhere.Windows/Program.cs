using Avalonia;
using Avalonia.Controls;
using Everywhere.Automation;
using Everywhere.Chat.Plugins;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.Initialization;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.ProcessIsolation.Watchdog;
using Everywhere.StrategyEngine;
using Everywhere.Windows.Automation;
using Everywhere.Windows.Chat.Plugins;
using Everywhere.Windows.Common;
using Everywhere.Windows.Initialization;
using Everywhere.Windows.Interop;
using Everywhere.Windows.ProcessIsolation.Input;
using Everywhere.Windows.ProcessIsolation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Serilog;

namespace Everywhere.Windows;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        NativeMessageBox.Register(WindowsNativeMessageBox.Show);
        Environment.ExitCode = RunAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var hostsControlPlatform = new WindowsHostsControlPlatform();
        var peerVerifier = WindowsNamedPipePeerVerifier.Instance;
        if (ProcessRoleCommandLine.ParseHostsControl(args) is { } hostsControlCommand)
        {
            return await HostsControlRunner.RunAsync(hostsControlCommand, hostsControlPlatform, peerVerifier).ConfigureAwait(false);
        }

        var role = ProcessRoleCommandLine.Parse(args);
        if (role is not ProcessRole.Main)
        {
            return await (role is ProcessRole.Input ?
                ProcessRoleHostRunner.RunAsync(role, args, peerVerifier, static () => new WindowsInputHostSession()) :
                RunAutomationHostAsync(args, peerVerifier)).ConfigureAwait(false);
        }

        await using var entrance = Entrance.Initialize(args);
        if (!entrance.IsPrimary)
        {
            return await entrance.ForwardAsync().ConfigureAwait(false);
        }

        return await RunMainAsync(args, hostsControlPlatform, peerVerifier).ConfigureAwait(false);
    }

    private static Task<int> RunAutomationHostAsync(string[] args, INamedPipePeerVerifier peerVerifier) => Task.Run(
        async () =>
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            {
                throw new InvalidOperationException("The Windows Automation Host must run on an MTA thread.");
            }

            return await ProcessRoleHostRunner
                .RunAsync(ProcessRole.Automation, args, peerVerifier, CreateAutomationHostSession)
                .ConfigureAwait(false);
        });

    private static IProcessRoleSession CreateAutomationHostSession()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
        {
            throw new InvalidOperationException("The Windows Automation Host must initialize UI Automation on an MTA thread.");
        }

        return new AutomationHostSession(new WindowsVisualElementBackend(), new WindowsVisualPickerResolver());
    }

    private static async Task<int> RunMainAsync(
        string[] args,
        WindowsHostsControlPlatform hostsControlPlatform,
        INamedPipePeerVerifier peerVerifier)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("Avalonia must be initialized on an STA thread.");
        }

        await using var serviceProvider = ServiceLocator.Build(x => x

            #region Basic

                .AddApplicationLogging()
                .AddSingleton<IHostsServiceModeManager>(hostsControlPlatform)
                .AddSingleton<INamedPipePeerVerifier>(peerVerifier)
                .AddProcessIsolation()
                .AddInputHostShortcutListener()
                .AddSingleton<WindowsScreenSelectionService>()
                .AddSingleton<IScreenSelectionService>(sp => sp.GetRequiredService<WindowsScreenSelectionService>())
                .AddSingleton<WindowsTextSelectionWatcher>()
                .AddSingleton<ITextSelectionWatcher>(sp => sp.GetRequiredService<WindowsTextSelectionWatcher>())
                .AddSingleton<IVisualElementBackend, WindowsVisualElementBackend>()
                .AddSingleton<INativeHelper, NativeHelper>()
                .AddSingleton<IWindowHelper, WindowHelper>()
                .AddSingleton<IPlatformUpdateHandler, WindowsUpdateHandler>()
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

            .AddTransient<BuiltInChatPlugin, EverythingPlugin>()

            #endregion

            #region Strategy Engine

            .AddStrategyEngine()

            #endregion

            #region Initialize

            .AddTransient<IAsyncInitializer, ChatWindowInitializer>()
            .AddTransient<IAsyncInitializer, UpdaterInitializer>()
            .AddTransient<IAsyncInitializer, ElevatedMainNotificationInitializer>()

            #endregion

        );

        RegisterUrlProtocol();

        var exitCode = BuildAvaloniaApp(serviceProvider).StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        if (Application.Current is App app)
        {
            await app.WaitForShutdownAsync().ConfigureAwait(false);
        }

        return exitCode;
    }

    private static AppBuilder BuildAvaloniaApp(IServiceProvider serviceProvider) =>
        AppBuilder.Configure(() => new App(serviceProvider))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Register the "sylinko-everywhere" protocol handler in Registry
    /// </summary>
    private static void RegisterUrlProtocol()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            const string CommandKeyPath = $@"Software\Classes\{UrlProtocolCallbackMessage.Scheme}";
            const string CommandSubPath = @"shell\open\command";
            var command = $"\"{exePath}\" \"%1\"";

            using (var existingKey = Registry.CurrentUser.OpenSubKey($@"{CommandKeyPath}\{CommandSubPath}", writable: false))
            {
                if (existingKey?.GetValue(null) is string existingValue && existingValue == command)
                {
                    return;
                }
            }

            using var registry = Registry.CurrentUser.CreateSubKey(CommandKeyPath);
            registry.SetValue(null, "URL: Sylinko Everywhere Protocol");
            registry.SetValue("URL Protocol", string.Empty);

            using var commandKey = registry.CreateSubKey(CommandSubPath);
            commandKey.SetValue(null, command);
        }
        catch (Exception ex)
        {
            Log.ForContext(typeof(Program)).Error(ex, "Failed to register URL protocol");
        }
    }
}
