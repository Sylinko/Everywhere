using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.TextSelection;
using Everywhere.Views;
using Lucide.Avalonia;
using ShadUI;

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
    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_TextSelection_Header);

    [SettingsItemIgnore]
    public IDynamicLocaleKey? DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_TextSelection_Description);

    public const int MaxVisibleActionCount = 12;

    /// <summary>
    /// Gets or sets the last acknowledged toolbar intent, independently of automatic attachment capture.
    /// </summary>
    [ObservableProperty]
    [SettingsItemIgnore]
    public partial bool IsToolbarEnabled { get; set; }

    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.TextSelectionSettings_IsToolbarEnabled_Header)]
    [SettingsItem(Group = LocaleKey.TextSelectionSettings_Group_Toolbar)]
    public SettingsControl<AsyncToggleSwitch> ToolbarMonitoringControl => new(
        () =>
        {
            var controller = GetRequiredService<TextSelectionController>();
            var control = new AsyncToggleSwitch
            {
                Command = controller.SetToolbarEnabledCommand,
                [!ToggleButton.IsCheckedProperty] = CompiledBinding.Create(
                    (TextSelectionController source) => source.IsToolbarEnabled,
                    source: controller,
                    mode: BindingMode.OneWay),
                [!AsyncToggleSwitch.IsBusyProperty] = CompiledBinding.Create(
                    (TextSelectionController source) => source.IsBusy,
                    source: controller),
            };
            return control;
        },
        isCacheEnabled: false);

    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.TextSelectionSettings_ToolbarPreview_Header)]
    [SettingsItem(
        IsVisibleBindingPath = nameof(IsToolbarEnabled),
        Classes = ["FullWidth", "NoHeading"],
        Group = LocaleKey.TextSelectionSettings_Group_Toolbar)]
    public SettingsControl<TextSelectionToolbarPreview> ToolbarPreview => new(ServiceProvider);

    /// <summary>
    /// Gets or sets the toolbar opacity, from 0.1 to fully opaque.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.TextSelectionSettings_ToolbarOpacity_Header)]
    [SettingsDoubleItem(
        IsVisibleBindingPath = nameof(IsToolbarEnabled),
        Min = 0.1,
        Max = 1,
        Step = 0.05,
        IsTextBoxVisible = false,
        Group = LocaleKey.TextSelectionSettings_Group_Toolbar)]
    public double ToolbarOpacity
    {
        get;
        set => SetProperty(ref field, double.IsNaN(value) ? 1d : Math.Clamp(value, 0.1d, 1d));
    } = 1d;

    /// <summary>
    /// Gets or sets whether the toolbar fades as the pointer moves away.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.TextSelectionSettings_IsToolbarOpacityAdaptive_Header,
        LocaleKey.TextSelectionSettings_IsToolbarOpacityAdaptive_Description)]
    [SettingsItem(
        IsVisibleBindingPath = nameof(IsToolbarEnabled),
        Group = LocaleKey.TextSelectionSettings_Group_Toolbar)]
    public partial bool IsToolbarOpacityAdaptive { get; set; }

    /// <summary>
    /// Gets or sets whether the freeform chat is enabled.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.TextSelectionSettings_IsFreeformChatEnabled_Header,
        LocaleKey.TextSelectionSettings_IsFreeformChatEnabled_Description)]
    [SettingsItem(
        IsVisibleBindingPath = nameof(IsToolbarEnabled),
        Group = LocaleKey.TextSelectionSettings_Group_Toolbar)]
    public partial bool IsFreeformChatEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of tools shown directly. Remaining tools are available in the menu.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(LocaleKey.TextSelectionSettings_VisibleActionCount_Header)]
    [SettingsIntegerItem(
        IsVisibleBindingPath = nameof(IsToolbarEnabled),
        Min = 0,
        Max = MaxVisibleActionCount,
        IsTextBoxVisible = false,
        Group = LocaleKey.TextSelectionSettings_Group_Toolbar)]
    public partial int VisibleActionCount { get; set; } = 5;

    [SettingsItemIgnore]
    public ObservableCollection<TextSelectionToolbarAction> ToolbarActions { get; } = [];

    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.Empty)]
    [SettingsItem(
        IsVisibleBindingPath = nameof(IsToolbarEnabled),
        Classes = ["FullWidth", "NoHeading"],
        Group = LocaleKey.TextSelectionSettings_Group_Toolbar)]
    public SettingsControl<TextSelectionToolbarActionsControl> ToolbarActionsControl => new(ServiceProvider);

    /// <summary>
    /// Gets or sets the last acknowledged automatic attachment intent.
    /// </summary>
    [ObservableProperty]
    [SettingsItemIgnore]
    public partial bool IsAutomaticAttachmentEnabled { get; set; }

    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.TextSelectionSettings_IsAutomaticAttachmentEnabled_Header,
        LocaleKey.TextSelectionSettings_IsAutomaticAttachmentEnabled_Description)]
    [SettingsItem(Group = LocaleKey.TextSelectionSettings_Group_ChatWindow)]
    public SettingsControl<AsyncToggleSwitch> AutomaticAttachmentControl => new(
        () =>
        {
            var controller = GetRequiredService<TextSelectionController>();
            var control = new AsyncToggleSwitch
            {
                Command = controller.SetAutomaticAttachmentEnabledCommand,
                [!ToggleButton.IsCheckedProperty] = CompiledBinding.Create(
                    (TextSelectionController source) => source.IsAutomaticAttachmentEnabled,
                    source: controller,
                    mode: BindingMode.OneWay),
                [!AsyncToggleSwitch.IsBusyProperty] = CompiledBinding.Create(
                    (TextSelectionController source) => source.IsBusy,
                    source: controller),
            };
            return control;
        },
        isCacheEnabled: false);

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.TextSelectionSettings_IsFullscreenExcluded_Header,
        LocaleKey.TextSelectionSettings_IsFullscreenExcluded_Description)]
    [SettingsItem(Group = LocaleKey.TextSelectionSettings_Group_Advanced)]
    public partial bool IsFullscreenExcluded { get; set; } = true;

    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.Empty)]
    [SettingsItem(Classes = ["FullWidth", "NoHeading"], Group = LocaleKey.TextSelectionSettings_Group_Advanced)]
    public SettingsControl<TextSelectionApplicationsControl> ExcludedApplicationsControl => new(() =>
    {
        var control = GetRequiredService<TextSelectionApplicationsControl>();
        control.HeaderKey = new DynamicLocaleKey(LocaleKey.TextSelectionSettings_ExcludedApplications_Header);
        control.DescriptionKey = new DynamicLocaleKey(LocaleKey.TextSelectionSettings_ExcludedApplications_Description);
        control.IsCompatibilityHintVisible = true;
        control[!TextSelectionApplicationsControl.ApplicationsProperty] = CompiledBinding.Create(
            (TextSelectionApplicationSettings settings) => settings.ExcludedApplications,
            source: Platform,
            mode: BindingMode.TwoWay);
        return control;
    });

    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.Empty)]
    [SettingsItem(
        IsVisibleBindingPath = nameof(IsToolbarEnabled),
        Classes = ["FullWidth", "NoHeading"],
        Group = LocaleKey.TextSelectionSettings_Group_Advanced)]
    public SettingsControl<TextSelectionApplicationsControl> ToolbarExcludedApplicationsControl => new(() =>
    {
        var control = GetRequiredService<TextSelectionApplicationsControl>();
        control.HeaderKey = new DynamicLocaleKey(LocaleKey.TextSelectionSettings_ToolbarExcludedApplications_Header);
        control.DescriptionKey = new DynamicLocaleKey(LocaleKey.TextSelectionSettings_ToolbarExcludedApplications_Description);
        control[!TextSelectionApplicationsControl.ApplicationsProperty] = CompiledBinding.Create(
            (TextSelectionApplicationSettings settings) => settings.ToolbarExcludedApplications,
            source: Platform,
            mode: BindingMode.TwoWay);
        return control;
    });

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
    /// Applications in which only the floating toolbar is suppressed; automatic attachment remains available.
    /// </summary>
    [ObservableProperty]
    public partial string[] ToolbarExcludedApplications { get; set; } = [];

    /// <summary>
    /// Retains existing compatibility overrides without exposing an editor in this release.
    /// </summary>
    [ObservableProperty]
    public partial string[] CopyFallbackExcludedApplications { get; set; } = [];

    /// <summary>
    /// Matches an identity captured with the selection, including manually entered executable names on Windows.
    /// </summary>
    public bool IsToolbarExcluded(string? identity)
    {
        if (string.IsNullOrEmpty(identity))
        {
            return false;
        }

#if WINDOWS
        var processName = Path.GetFileName(identity);
        return ToolbarExcludedApplications.Any(application =>
            string.Equals(application, identity, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(application, processName, StringComparison.OrdinalIgnoreCase));
#elif MACOS
        return ToolbarExcludedApplications.Contains(identity, StringComparer.Ordinal);
#else
#error Implement toolbar application identity matching for this platform.
#endif
    }
}

/// <summary>
/// Windows rules match absolute executable paths or executable file names, ignoring case.
/// </summary>
public sealed class WindowsTextSelectionSettings : TextSelectionApplicationSettings;

/// <summary>
/// macOS rules match exact, case-sensitive application bundle identifiers.
/// </summary>
public sealed class MacTextSelectionSettings : TextSelectionApplicationSettings;