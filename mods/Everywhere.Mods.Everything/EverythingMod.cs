using Avalonia.Threading;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Configuration.Engine;
using Everywhere.I18N;
using Everywhere.Modding.Abstractions;
using Everywhere.ProcessIsolation.Watchdog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mods.Everything;

/// <summary>
/// Owns Everything tool registration, in-flight queries, settings registration, and the optional private Everything process.
/// User-owned Everything processes are never registered with Watchdog or stopped by this mod.
/// </summary>
public sealed class EverythingMod : IMod, IAsyncDisposable
{
    private IModContext? _context;
    private EverythingConnection? _connection;
    private EverythingChatPlugin? _plugin;
    private IAsyncDisposable? _pluginRegistration;
    private IAsyncDisposable? _settingsRegistration;
    private SettingsEngine<EverythingSettings>? _settings;

    /// <inheritdoc />
    public async ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken)
    {
        _context = context;
        context.Register(
            LocaleManager.RegisterProvider(
                typeof(EverythingMod).Assembly,
                static () => new ResourceManagerLocaleResourceProvider(
                    "Everywhere.Mods.Everything.I18N.Strings",
                    typeof(EverythingMod).Assembly)));

        var settings = new EverythingSettings();
        _settings = await context.OpenSettingsAsync(
                settings,
                new SemanticVersion(1),
                EverythingSettingsJsonSerializerContext.Default.Options,
                new EverythingSettingsDescriptors(),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (context.Services.GetService<IModSettingsRegistry>() is { } settingsRegistry)
        {
            _settingsRegistration = settingsRegistry.Register(context, () => settings.SettingsItems);
        }

        _connection = new EverythingConnection(
            context,
            context.Services.GetRequiredService<IWatchdogManager>(),
            settings);
        _plugin = new EverythingChatPlugin(
            _connection,
            settings,
            context.Services.GetRequiredService<ILoggerFactory>().CreateLogger(context.ModId));
        _pluginRegistration = context.Services.GetRequiredService<IChatPluginManager>().RegisterChatPlugin(_plugin);
        context.Register(_pluginRegistration);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _connection?.Stop();
        try
        {
            if (_settingsRegistration is { } settingsRegistration)
            {
                await settingsRegistration.DisposeAsync().ConfigureAwait(false);
            }

            if (_pluginRegistration is { } pluginRegistration)
            {
                if (_context?.StopReason == ModStopReason.Exit)
                {
                    // Process exit must not wait for a dispatcher that may already have stopped.
                    await pluginRegistration.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    // Remove discovery on the UI thread so the chat page detaches its settings before final saving.
                    await Dispatcher.UIThread.InvokeAsync(async () => await pluginRegistration.DisposeAsync());
                }
            }

            if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
            _plugin?.Dispose();
        }
        finally
        {
            // Saving also runs during normal process exit, when automatic contribution cleanup is skipped.
            if (_settings is not null) await _settings.DisposeAsync().ConfigureAwait(false);
            _settings = null;
            _settingsRegistration = null;
            _pluginRegistration = null;
            _connection = null;
            _plugin = null;
            _context = null;
        }
    }
}
