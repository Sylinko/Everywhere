using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Interop;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class TextSelectionToolbarSettings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider), ISettingsCategory
{
    [SettingsItemIgnore]
    public int Index => 5;

    [SettingsItemIgnore]
    public LucideIconKind Icon => LucideIconKind.TextSelect;

    [SettingsItemIgnore]
    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_TextSelectionToolbar_Header);

    [SettingsItemIgnore]
    public IDynamicLocaleKey? DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_TextSelectionToolbar_Description);

    /// <summary>
    /// Whether this platform can observe the global input needed to dismiss the toolbar.
    /// </summary>
    /// <remarks>
    /// Gates the master toggle so the user cannot switch on a feature that would decline to arm. The
    /// stored value is deliberately left untouched when unsupported: a settings file shared with a
    /// Windows machine keeps the user's preference there.
    /// </remarks>
    [JsonIgnore]
    [SettingsItemIgnore]
    public bool IsSupported => GetRequiredService<IOverlayDismissWatcher>().IsSupported;

    /// <summary>
    /// Master toggle. Disabled by default because the feature installs global input hooks,
    /// which the user should opt into explicitly.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.TextSelectionToolbarSettings_IsEnabled_Header,
        LocaleKey.TextSelectionToolbarSettings_IsEnabled_Description)]
    [SettingsItem(IsEnabledBindingPath = nameof(IsSupported))]
    public partial bool IsEnabled { get; set; }

    /// <summary>Lower bound of <see cref="MaxActionCount"/>. A toolbar with no actions is pointless.</summary>
    public const int MinActionCount = 1;

    /// <summary>
    /// Upper bound of <see cref="MaxActionCount"/>. Beyond this the toolbar grows wide enough to obscure
    /// the text it describes.
    /// </summary>
    public const int MaxAllowedActionCount = 8;

    /// <summary>
    /// How many action buttons the toolbar may show at once.
    /// </summary>
    /// <remarks>
    /// Consumers must clamp between <see cref="MinActionCount"/> and <see cref="MaxAllowedActionCount"/>:
    /// the declared range drives the settings UI, but the settings binder assigns persisted values
    /// without enforcing it, so a hand-edited settings file can carry a value outside the range.
    /// </remarks>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.TextSelectionToolbarSettings_MaxActionCount_Header,
        LocaleKey.TextSelectionToolbarSettings_MaxActionCount_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsEnabled), Group = "_")]
    [SettingsIntegerItem(Min = MinActionCount, Max = MaxAllowedActionCount)]
    public partial int MaxActionCount { get; set; } = 5;

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.TextSelectionToolbarSettings_ShowActionLabels_Header,
        LocaleKey.TextSelectionToolbarSettings_ShowActionLabels_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsEnabled), Group = "_")]
    public partial bool ShowActionLabels { get; set; } = true;
}
