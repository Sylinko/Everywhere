using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Interop;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class ShortcutSettings : SettingsBase, ISettingsCategory
{
    [SettingsItemIgnore]
    public int Index => 2;

    [SettingsItemIgnore]
    public LucideIconKind Icon => LucideIconKind.Keyboard;

    [SettingsItemIgnore]
    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_Shortcut_Header);

    [SettingsItemIgnore]
    public IDynamicResourceKey? DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_Shortcut_Description);

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_ChatWindow_Header,
        LocaleKey.ShortcutSettings_ChatWindow_Desription)]
    [SettingsItem(Group = "_")]
    [SettingsTemplatedItem]
    public partial KeyboardShortcut ChatWindow { get; set; } = new(Key.E, KeyModifiers.Control | KeyModifiers.Shift);

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_PickVisualElement_Header,
        LocaleKey.ShortcutSettings_PickVisualElement_Desription)]
    [SettingsItem(Group = "_")]
    [SettingsTemplatedItem]
    public partial KeyboardShortcut PickVisualElement { get; set; }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ShortcutSettings_TakeScreenshot_Header,
        LocaleKey.ShortcutSettings_TakeScreenshot_Desription)]
    [SettingsItem(Group = "_")]
    [SettingsTemplatedItem]
    public partial KeyboardShortcut TakeScreenshot { get; set; }
}