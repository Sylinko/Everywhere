using Avalonia.Input;
using Everywhere.Interop;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class ShortcutSettings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider), ISettingsCategory
{
    [SettingsItemIgnore]
    public int Index => 2;

    [SettingsItemIgnore]
    public LucideIconKind Icon => LucideIconKind.Keyboard;

    [SettingsItemIgnore]
    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_Shortcut_Header);

    [SettingsItemIgnore]
    public IDynamicLocaleKey? DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsCategory_Settings_Shortcut_Description);

    [DynamicLocaleKey(
        LocaleKey.ShortcutSettings_ChatWindow_Header,
        LocaleKey.ShortcutSettings_ChatWindow_Desription)]
    [SettingsTemplatedItem]
    public CompositeKeyboardShortcut ChatWindow { get; set; } = new KeyboardShortcut(Key.E, KeyModifiers.Control | KeyModifiers.Shift);

    [DynamicLocaleKey(
        LocaleKey.ShortcutSettings_PickVisualElement_Header,
        LocaleKey.ShortcutSettings_PickVisualElement_Desription)]
    [SettingsTemplatedItem]
    public CompositeKeyboardShortcut PickVisualElement { get; } = new();

    [DynamicLocaleKey(
        LocaleKey.ShortcutSettings_TakeScreenshot_Header,
        LocaleKey.ShortcutSettings_TakeScreenshot_Desription)]
    [SettingsTemplatedItem]
    public CompositeKeyboardShortcut TakeScreenshot { get; } = new();
}