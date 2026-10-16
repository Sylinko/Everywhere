using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.Views;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

/// <summary>
/// Stores selection behavior and independently persisted platform application rules.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class TextSelectionSettings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider), ISettingsCategory
{
    [SettingsItemIgnore]
    public int Index => 5;

    [SettingsItemIgnore]
    public LucideIconKind Icon => LucideIconKind.TextSelect;

    [SettingsItemIgnore]
    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.TextSelectionSettings_Title);

    [SettingsItemIgnore]
    public IDynamicLocaleKey? DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.TextSelectionSettings_Description);

    /// <summary>
    /// Gets or sets the last acknowledged monitoring intent.
    /// </summary>
    [ObservableProperty]
    [SettingsItemIgnore]
    public partial bool IsEnabled { get; set; }

    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.TextSelectionSettings_Enable, LocaleKey.TextSelectionSettings_EnableDescription)]
    public SettingsControl<TextSelectionMonitoringControl> Monitoring { get; } = new();

    [ObservableProperty]
    [DynamicLocaleKey(LocaleKey.TextSelectionSettings_ExcludeFullscreen, LocaleKey.TextSelectionSettings_ExcludeFullscreenDescription)]
    public partial bool IsFullscreenExcluded { get; set; } = true;

    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.Empty)]
    [SettingsItem(Classes = ["FullWidth"])]
    public SettingsControl<TextSelectionApplicationsControl> Applications { get; } = new();

    // Both subtrees remain in the serialized model so importing settings never discards another platform's rules.
    [SettingsItemIgnore]
    public WindowsTextSelectionSettings Windows { get; } = new();

    [SettingsItemIgnore]
    public MacTextSelectionSettings Mac { get; } = new();

    [JsonIgnore]
    [SettingsItemIgnore]
    public TextSelectionApplicationSettings Platform
    {
        get
        {
#if MACOS
            return Mac;
#elif WINDOWS
            return Windows;
#else
#error Implement text-selection application settings for this platform.
#endif
        }
    }

    /// <summary>
    /// Creates the native monitoring policy for the current platform.
    /// </summary>
    public TextSelectionMonitoringConfiguration CreateMonitoringConfiguration()
    {
        return new TextSelectionMonitoringConfiguration
        {
            IsFullscreenApplicationExcluded = IsFullscreenExcluded,
            ExcludedApplications = Platform.ExcludedApplications,
            CopyFallbackExcludedApplications = Platform.CopyFallbackExcludedApplications,
        };
    }
}

/// <summary>
/// Stores exact native application identities. Updates replace arrays atomically for settings observation.
/// </summary>
public abstract partial class TextSelectionApplicationSettings : ObservableObject
{
    [ObservableProperty]
    public partial string[] ExcludedApplications { get; set; } = [];

    /// <summary>
    /// Retains existing compatibility overrides without exposing an editor in this release.
    /// </summary>
    [ObservableProperty]
    public partial string[] CopyFallbackExcludedApplications { get; set; } = [];
}

/// <summary>
/// Windows rules match absolute executable paths or executable file names, ignoring case.
/// </summary>
public sealed class WindowsTextSelectionSettings : TextSelectionApplicationSettings;

/// <summary>
/// macOS rules match exact, case-sensitive application bundle identifiers.
/// </summary>
public sealed class MacTextSelectionSettings : TextSelectionApplicationSettings;