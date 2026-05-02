using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Extensions;
using Everywhere.Initialization;
using Everywhere.Interop;
using Everywhere.Linux.Chat.Plugins;
using Everywhere.Linux.Common;
using Everywhere.Linux.Interop;
using Everywhere.StrategyEngine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace Everywhere.Linux;

public static class Program
{
    
    public static IServiceCollection AddWindowEventHelper(this IServiceCollection services)
    {
        // CheckEnv
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("Fatal Error: Not Linux OS platform.");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            throw new InvalidOperationException("Fatal Error: DISPLAY environment variable is not set. You should start in GUI env.");
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        // for future use
        // var desktop = Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP");
        services.AddSingleton<X11WindowBackend>();
        if (session != "x11")
        {
            // not x11, may be not fully supported
            Log.Logger.Warning("Not X11 Session, Maybe not supported well");
        }
        services.AddSingleton<IEventHelper>(sp => sp.GetRequiredService<X11WindowBackend>());
        services.AddSingleton<IWindowBackend>(sp => sp.GetRequiredService<X11WindowBackend>());
        services.AddSingleton<IWindowHelper>(sp => sp.GetRequiredService<X11WindowBackend>());
        return services;
    }
    
    [STAThread]
    public static async Task Main(string[] args)
    {
        await Entrance.InitializeAsync(args);

        ServiceLocator.Build(x => x

                #region Basic

                .AddLogging(builder => builder
                    .AddSerilog(dispose: true)
                    .AddFilter<SerilogLoggerProvider>("Microsoft.EntityFrameworkCore", LogLevel.Debug))
                .AddWindowEventHelper()
                .AddSingleton<IVisualElementContext, VisualElementContext>()
                .AddSingleton<IShortcutListener, ShortcutListener>()
                .AddSingleton<INativeHelper, NativeHelper>()
                .AddSingleton<IPlatformUpdateHandler, LinuxUpdateHandler>()
                .AddSingleton<ISoftwareUpdater, SoftwareUpdater>()
                .AddSettings()
                .AddWatchdogManager()
                .ConfigureNetwork()
                .AddAvaloniaBasicServices()
                .AddViewsAndViewModels()
                .AddDatabaseAndStorage()
                .AddChatEssentials()

                #endregion

                #region Chat Plugins

                .AddTransient<BuiltInChatPlugin, FdFindPlugin>()

                #endregion

                #region Chat

                .AddSingleton<IKernelMixinFactory, KernelMixinFactory>()
                .AddSingleton<IChatPluginManager, ChatPluginManager>()
                .AddSingleton<IChatService, ChatService>()
                .AddChatContextManager()

                #endregion

                #region Strategy Engine

                .AddStrategyEngine()

                #endregion

                #region Initialize

                .AddTransient<IAsyncInitializer, ChatWindowInitializer>()
                .AddTransient<IAsyncInitializer, UpdaterInitializer>()

            #endregion

        );

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
