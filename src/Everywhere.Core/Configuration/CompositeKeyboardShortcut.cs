using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Interop;

namespace Everywhere.Configuration;

/// <summary>
/// Represents a switchable composite keyboard shortcut that includes a main shortcut and an alternative shortcut.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class CompositeKeyboardShortcut : ObservableObject
{
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.CompositeKeyboardShortcut_IsEnabled_Header,
        LocaleKey.CompositeKeyboardShortcut_IsEnabled_Description)]
    [SettingsItem(Group = "_")]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.CompositeKeyboardShortcut_Main_Header,
        LocaleKey.CompositeKeyboardShortcut_Main_Description)]
    [SettingsItem(Group = "_")]
    [SettingsTemplatedItem]
    public partial KeyboardShortcut Main { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.CompositeKeyboardShortcut_Alternative_Header,
        LocaleKey.CompositeKeyboardShortcut_Alternative_Description)]
    [SettingsItem(Group = "_")]
    [SettingsTemplatedItem]
    public partial KeyboardShortcut Alternative { get; set; }

    public static implicit operator CompositeKeyboardShortcut(KeyboardShortcut shortcut) => new() { Main = shortcut };
}