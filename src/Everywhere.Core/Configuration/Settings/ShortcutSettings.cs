using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Interop;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class ShortcutSettings : SettingsBase, ISettingsCategory
{
    [HiddenSettingsItem]
    public int Index => 2;

    [HiddenSettingsItem]
    public LucideIconKind Icon => LucideIconKind.Keyboard;

    [HiddenSettingsItem]
    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_Shortcut_Header);

    [HiddenSettingsItem]
    public IDynamicResourceKey? DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_Shortcut_Description);

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_QuickMode_Header,
        LocaleKey.ShortcutSettings_QuickMode_Desription)]
    [SettingsTemplatedItem]
    public partial KeyboardShortcut QuickMode { get; set; } = new(Key.Q, KeyModifiers.Control | KeyModifiers.Shift);

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_ChatWindow_Header,
        LocaleKey.ShortcutSettings_ChatWindow_Desription)]
    [SettingsTemplatedItem]
    public partial KeyboardShortcut ChatWindow { get; set; } = new(Key.E, KeyModifiers.Control | KeyModifiers.Shift);

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_PickVisualElement_Header,
        LocaleKey.ShortcutSettings_PickVisualElement_Desription)]
    [SettingsTemplatedItem]
    public partial KeyboardShortcut PickVisualElement { get; set; }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_TakeScreenshot_Header,
        LocaleKey.ShortcutSettings_TakeScreenshot_Desription)]
    [SettingsTemplatedItem]
    public partial KeyboardShortcut TakeScreenshot { get; set; }
}