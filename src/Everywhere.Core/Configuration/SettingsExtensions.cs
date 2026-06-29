using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using Everywhere.Common;
using Everywhere.Initialization;
using Everywhere.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WritableJsonConfiguration;

namespace Everywhere.Configuration;

public static class SettingsExtensions
{
#if WINDOWS
    [SupportedOSPlatform("windows")]
#endif
    public static IServiceCollection AddSettings(this IServiceCollection services) => services
        .AddKeyedSingleton<IConfiguration>(
            typeof(Settings),
            (xx, _) =>
            {
                // Forward compatibility: use FallbackGuidConverter to handle invalid GUIDs and set them to Guid.Empty
                TypeDescriptor.AddAttributes(typeof(Guid), new TypeConverterAttribute(typeof(FallbackGuidConverter)));

                var settingsJsonPath = Path.Combine(RuntimeConstants.WritableFolderPath, "settings.json");
                var loggerFactory = xx.GetRequiredService<ILoggerFactory>();

                // Run Migrations
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

                IConfiguration configuration;
                try
                {
                    configuration = WritableJsonConfigurationFabric.Create(settingsJsonPath, loggerFactory: loggerFactory);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    File.Delete(settingsJsonPath);
                    configuration = WritableJsonConfigurationFabric.Create(settingsJsonPath, loggerFactory: loggerFactory);
                }
                return configuration;
            })
        .AddSingleton<Settings>(xx =>
        {
            var configuration = xx.GetRequiredKeyedService<IConfiguration>(typeof(Settings));
            var settings = new Settings(xx);
            configuration.Bind(settings);
            return settings;
        })
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
        .AddTransient<IAsyncInitializer, SettingsInitializer>()
        .AddTransient<IAsyncInitializer, CustomAssistantInitializer>();
}
