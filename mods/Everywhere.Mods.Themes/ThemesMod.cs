using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Configuration.Engine;
using Everywhere.I18N;
using Everywhere.Modding.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Mods.Themes;

/// <summary>
/// Initializes built-in themes, custom JSON theme loading, public <see cref="ThemeRegistry"/>
/// lifetime, and dynamic theme application.
/// </summary>
public sealed class ThemesMod : IMod, IAsyncDisposable
{
    private const string CustomThemesFolderName = "themes";

    private IModContext? _context;
    private ILogger? _logger;
    private string _customThemesDirectory = string.Empty;
    private SettingsEngine<ThemeSettings>? _settings;
    private ThemeApplicator? _applicator;
    private IDisposable? _builtInRegistration;
    private IDisposable? _customJsonRegistration;
    private IAsyncDisposable? _settingsRegistration;

    /// <inheritdoc />
    public async ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _logger = context.Services.GetRequiredService<ILoggerFactory>().CreateLogger(context.ModId);
        _customThemesDirectory = Path.Combine(context.DataDirectory, CustomThemesFolderName);

        context.Register(
            LocaleManager.RegisterProvider(
                typeof(ThemesMod).Assembly,
                static () => new ResourceManagerLocaleResourceProvider(
                    "Everywhere.Mods.Themes.I18N.Strings",
                    typeof(ThemesMod).Assembly)));

        _builtInRegistration = ThemeRegistry.RegisterThemes(BuiltInThemes.All);
        ReloadCustomJsonThemes();

        var settings = new ThemeSettings(_customThemesDirectory, ReloadCustomJsonThemes);
        _settings = await context.OpenSettingsAsync(
                settings,
                new SemanticVersion(1),
                ThemeSettingsJsonSerializerContext.Default.Options,
                new ThemeSettingsDescriptors(),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _applicator = new ThemeApplicator(settings);
        await _applicator.AttachAsync(cancellationToken).ConfigureAwait(false);

        if (context.Services.GetService<IModSettingsRegistry>() is { } settingsRegistry)
        {
            _settingsRegistration = settingsRegistry.Register(context, () => settings.SettingsItems);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_settingsRegistration is { } settingsRegistration)
            {
                await settingsRegistration.DisposeAsync().ConfigureAwait(false);
            }

            if (_applicator is not null)
            {
                if (_context?.StopReason == ModStopReason.Exit)
                {
                    // Process exit skips UI resource reversal because the dispatcher may no longer be running.
                }
                else
                {
                    await _applicator.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _customJsonRegistration?.Dispose();
            _builtInRegistration?.Dispose();

            if (_settings is not null)
            {
                await _settings.DisposeAsync().ConfigureAwait(false);
            }

            _settings = null;
            _settingsRegistration = null;
            _applicator = null;
            _customJsonRegistration = null;
            _builtInRegistration = null;
            _context = null;
            _logger = null;
        }
    }

    private void ReloadCustomJsonThemes()
    {
        var logger = _logger;
        if (logger is null || string.IsNullOrWhiteSpace(_customThemesDirectory))
        {
            return;
        }

        // Unregister previous JSON themes first so updated definitions keep their IDs cleanly.
        _customJsonRegistration?.Dispose();
        _customJsonRegistration = null;

        var jsonThemes = ThemeJsonLoader.LoadFromDirectory(_customThemesDirectory, logger);
        if (jsonThemes.Count == 0)
        {
            return;
        }

        try
        {
            _customJsonRegistration = ThemeRegistry.RegisterThemes(jsonThemes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to register one or more custom JSON themes from '{Directory}'.", _customThemesDirectory);
        }
    }
}
