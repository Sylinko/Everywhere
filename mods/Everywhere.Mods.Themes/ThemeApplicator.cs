using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Everywhere.Views;

namespace Everywhere.Mods.Themes;

/// <summary>
/// Manages injection, live switching, and collectible-safe removal of compiled theme resources
/// inside <see cref="Application.Resources"/>.
/// </summary>
internal sealed class ThemeApplicator(ThemeSettings settings) : IAsyncDisposable
{
    private ResourceDictionary? _activeOverlayDictionary;
    private bool _isAttached;
    private bool _isDisposed;

    /// <summary>
    /// Attaches event listeners and applies the currently configured light/dark themes on the UI thread.
    /// </summary>
    public Task AttachAsync(CancellationToken cancellationToken = default)
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_isDisposed || _isAttached)
            {
                return;
            }

            _isAttached = true;
            settings.PropertyChanged += HandleSettingsPropertyChanged;
            ThemeRegistry.Changed += HandleRegistryChanged;

            if (TryGetAppearanceManager() is { } appearanceManager)
            {
                appearanceManager.AccentColorChanged += HandleAccentColorChanged;
            }

            ApplyCurrentThemesOnUiThread();
        }).GetTask();
    }

    /// <summary>
    /// Detaches handlers and removes the injected theme dictionary on hot unload.
    /// Process exit skips UI reversal when the dispatcher is unavailable.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        settings.PropertyChanged -= HandleSettingsPropertyChanged;
        ThemeRegistry.Changed -= HandleRegistryChanged;

        if (Dispatcher.UIThread.CheckAccess())
        {
            DetachUiResources();
        }
        else if (Application.Current is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(DetachUiResources);
        }
    }

    private void DetachUiResources()
    {
        if (TryGetAppearanceManager() is { } appearanceManager)
        {
            appearanceManager.AccentColorChanged -= HandleAccentColorChanged;
        }

        if (_activeOverlayDictionary is { } active &&
            Application.Current?.Resources.MergedDictionaries is { } merged)
        {
            merged.Remove(active);
        }

        _activeOverlayDictionary = null;
        _isAttached = false;
    }

    private void HandleSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ThemeSettings.LightThemeId) or
            nameof(ThemeSettings.DarkThemeId) or
            nameof(ThemeSettings.ShouldOverrideAccentColor))
        {
            ScheduleApplyCurrentThemes();
        }
    }

    private void HandleRegistryChanged(object? sender, EventArgs e)
    {
        ScheduleApplyCurrentThemes();
    }

    private void HandleAccentColorChanged(object? sender, Color accent)
    {
        if (settings.ShouldOverrideAccentColor)
        {
            ScheduleApplyCurrentThemes();
        }
    }

    private void ScheduleApplyCurrentThemes()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyCurrentThemesOnUiThread();
            return;
        }

        Dispatcher.UIThread.Post(ApplyCurrentThemesOnUiThread);
    }

    private void ApplyCurrentThemesOnUiThread()
    {
        if (_isDisposed || Application.Current is not { } application)
        {
            return;
        }

        var lightTheme = ResolveTheme(settings.LightThemeId, ThemeBaseVariant.Light, BuiltInThemes.DefaultLightId);
        var darkTheme = ResolveTheme(settings.DarkThemeId, ThemeBaseVariant.Dark, BuiltInThemes.DefaultDarkId);

        // Normalize persisted selections when a referenced third-party or JSON theme disappears on reload.
        if (!string.Equals(settings.LightThemeId, lightTheme.Id, StringComparison.OrdinalIgnoreCase))
        {
            settings.LightThemeId = lightTheme.Id;
        }

        if (!string.Equals(settings.DarkThemeId, darkTheme.Id, StringComparison.OrdinalIgnoreCase))
        {
            settings.DarkThemeId = darkTheme.Id;
        }

        var accentOverride = settings.ShouldOverrideAccentColor ? TryGetAppearanceManager()?.EffectiveAccentColor : null;

        // Always recreate a pure Avalonia ResourceDictionary containing only host/framework types so
        // dynamic theme updates fire reliably and no mod-defined types are retained in Application.Resources.
        var nextOverlay = new ResourceDictionary();
        if (!lightTheme.IsHostDefault)
        {
            nextOverlay.ThemeDictionaries[ThemeVariant.Light] = ShadThemeCompiler.Compile(lightTheme, accentOverride);
        }

        if (!darkTheme.IsHostDefault)
        {
            nextOverlay.ThemeDictionaries[ThemeVariant.Dark] = ShadThemeCompiler.Compile(darkTheme, accentOverride);
        }

        var merged = application.Resources.MergedDictionaries;
        var previous = _activeOverlayDictionary;
        if (nextOverlay.ThemeDictionaries.Count == 0)
        {
            if (previous is not null)
            {
                merged.Remove(previous);
                _activeOverlayDictionary = null;
            }

            return;
        }

        var existingIndex = previous is not null ? merged.IndexOf(previous) : -1;
        if (existingIndex >= 0)
        {
            merged[existingIndex] = nextOverlay;
        }
        else
        {
            merged.Add(nextOverlay);
        }

        _activeOverlayDictionary = nextOverlay;
    }

    private static ThemeDefinition ResolveTheme(string? requestedId, ThemeBaseVariant expectedVariant, string fallbackId)
    {
        var candidate = ThemeRegistry.FindById(requestedId);
        if (candidate is not null && candidate.Variant == expectedVariant)
        {
            return candidate;
        }

        return ThemeRegistry.FindById(fallbackId)
            ?? BuiltInThemes.All.First(t => t.Variant == expectedVariant && t.IsHostDefault);
    }

    private static AppearanceManager? TryGetAppearanceManager()
    {
        try
        {
            return AppearanceManager.Shared;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
