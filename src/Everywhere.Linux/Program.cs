using Avalonia;
using Avalonia.Controls;
using Everywhere.Cloud.DependencyInjection;
using Everywhere.Common;
using Everywhere.DependencyInjection;
using Everywhere.Linux.Chat.Plugins;
using Everywhere.Linux.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Linux;

public static class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        await Entrance.InitializeAsync(args);

        var serviceProvider = CreateServiceProvider();
        ServiceLocator.SetProvider(serviceProvider);

        BuildAvaloniaApp(serviceProvider).StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        ApplicationServiceCollection.Configure(services);
        CloudServiceCollection.Configure(services);

        var coreComposition = new CoreComposition();
        var cloudComposition = new CloudComposition();
        var linuxComposition = new LinuxComposition();

        coreComposition.CreateBuilder(services);
        cloudComposition.CreateBuilder(services);
        linuxComposition.CreateBuilder(services);
        ApplicationServiceCollection.ConfigureCoreAliases(services);
        CloudServiceCollection.ConfigureAliases(services);
        ApplicationServiceCollection.ConfigurePlatformAliases<FdFindPlugin>(services);

        var serviceProvider = services.BuildServiceProvider();
        coreComposition.ServiceProvider = serviceProvider;
        cloudComposition.ServiceProvider = serviceProvider;
        linuxComposition.ServiceProvider = serviceProvider;
        return serviceProvider;
    }

    private static AppBuilder BuildAvaloniaApp(IServiceProvider serviceProvider) =>
        AppBuilder.Configure(() => new App(serviceProvider))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
