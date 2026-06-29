using System.Runtime.Versioning;
using Everywhere.Common;
using Everywhere.Configuration.Engine;
using Everywhere.Initialization;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Configuration;

public static class SettingsExtensions
{
#if WINDOWS
    [SupportedOSPlatform("windows")]
#endif
    public static IServiceCollection AddSettings(this IServiceCollection services) => services
        .AddSingleton(xx =>
        {
            var settingsJsonPath = Path.Combine(RuntimeConstants.WritableFolderPath, "settings.json");
            var loggerFactory = xx.GetRequiredService<ILoggerFactory>();

            try
            {
                var migrations = typeof(SettingsExtensions).Assembly.GetTypes()
                    .Where(t => typeof(SettingsMigration).IsAssignableFrom(t) && !t.IsAbstract)
                    .Select(Activator.CreateInstance)
                    .Cast<SettingsMigration>();

                var migrator = new SettingsMigrator(settingsJsonPath, migrations, loggerFactory.CreateLogger<SettingsMigrator>());
                migrator.Migrate();
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("SettingsMigration").LogError(ex, "Error running settings migrations");
            }

            return SettingsEngine.Load(settingsJsonPath, xx, loggerFactory);
        })
        .AddSingleton(sp => sp.GetRequiredService<SettingsEngine>().Settings)
        .AddTransient<SoftwareUpdateControl>()
#if WINDOWS
        .AddTransient<RestartAsAdministratorControl>()
#endif
        .AddTransient<OpenWebBrowserControl>()
        .AddTransient<DebugFeaturesControl>()
        .AddSingleton<PersistentKeyValueStorage>()
        .AddSingleton<IKeyValueStorage>(xx => xx.GetRequiredService<PersistentKeyValueStorage>())
        .AddTransient<IAsyncInitializer>(xx => xx.GetRequiredService<PersistentKeyValueStorage>())
        .AddSingleton<PersistentState>()
        .AddTransient<IAsyncInitializer, CustomAssistantInitializer>();
}