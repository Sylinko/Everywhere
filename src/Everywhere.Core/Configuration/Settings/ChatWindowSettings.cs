using System.Diagnostics.Metrics;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class ChatWindowSettings : SettingsBase, ISettingsCategory
{
    [SettingsItemIgnore]
    public int Index => 3;

    [SettingsItemIgnore]
    public LucideIconKind Icon => LucideIconKind.MessageCircle;

    [SettingsItemIgnore]
    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_ChatWindow_Header);

    [SettingsItemIgnore]
    public IDynamicResourceKey? DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.SettingsCategory_Settings_ChatWindow_Description);

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ChatWindowSettings_WindowPinMode_Header,
        LocaleKey.ChatWindowSettings_WindowPinMode_Description)]
    [SettingsItem(Group = LocaleKey.ChatWindowSettings_Group_Behavior)]
    public partial ChatWindowPinMode WindowPinMode { get; set; }

    /// <summary>
    /// Temporary chat mode when creating a new chat.
    /// </summary>
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ChatWindowSettings_TemporaryChatMode_Header,
        LocaleKey.ChatWindowSettings_TemporaryChatMode_Description)]
    [SettingsItem(Group = LocaleKey.ChatWindowSettings_Group_Behavior)]
    public partial TemporaryChatMode TemporaryChatMode { get; set; }

    /// <summary>
    /// When enabled, automatically generate chat title based on the content of the first message.
    /// </summary>
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ChatWindowSettings_AutomaticallyGenerateTitle_Header,
        LocaleKey.ChatWindowSettings_AutomaticallyGenerateTitle_Description)]
    [SettingsItem(Group = LocaleKey.ChatWindowSettings_Group_Behavior)]
    public partial bool AutomaticallyGenerateTitle { get; set; } = true;

    /// <summary>
    /// When enabled, automatically add focused element as attachment when opening chat window.
    /// </summary>
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ChatWindowSettings_AutomaticallyAddElement_Header,
        LocaleKey.ChatWindowSettings_AutomaticallyAddElement_Description)]
    [SettingsItem(Group = LocaleKey.ChatWindowSettings_Group_Attachment)]
    public partial bool AutomaticallyAddElement { get; set; } = true;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ChatWindowSettings_AutomaticallyAddTextSelection_Header,
        LocaleKey.ChatWindowSettings_AutomaticallyAddTextSelection_Description)]
    [SettingsItem(Group = LocaleKey.ChatWindowSettings_Group_Attachment, IsExperimental = true)]
    public partial bool AutomaticallyAddTextSelection { get; set; }

    /// <summary>
    /// When enabled, always start a new chat when opening chat window.
    /// </summary>
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ChatWindowSettings_AlwaysStartNewChat_Header,
        LocaleKey.ChatWindowSettings_AlwaysStartNewChat_Description)]
    [SettingsItem(Group = LocaleKey.ChatWindowSettings_Group_Attachment)]
    public partial bool AlwaysStartNewChat { get; set; }

    /// <summary>
    /// When enabled, show chat statistics in the chat window.
    /// </summary>
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.ChatWindowSettings_ShowChatStatistics_Header,
        LocaleKey.ChatWindowSettings_ShowChatStatistics_Description)]
    [SettingsItem(Group = LocaleKey.ChatWindowSettings_Group_Display)]
    public partial bool ShowChatStatistics { get; set; } = true;

    [DynamicResourceKey(
        LocaleKey.ChatWindowSettings_EnableVisualElementPickAnimation_Header,
        LocaleKey.ChatWindowSettings_EnableVisualElementPickAnimation_Description)]
    [SettingsItem(Group = LocaleKey.ChatWindowSettings_Group_Display)]
    public bool EnableVisualElementPickAnimation
    {
        get;
        set
        {
            if (SetProperty(ref field, value)) _enableVisualElementPickAnimationGauge.Record(value ? 1 : 0);
        }
    } = true;

    [DynamicResourceKey(
        LocaleKey.ChatWindowSettings_EnableVisualContextAnimation_Header,
        LocaleKey.ChatWindowSettings_EnableVisualContextAnimation_Description)]
    [SettingsItem(Group = LocaleKey.ChatWindowSettings_Group_Display)]
    public bool EnableVisualContextAnimation
    {
        get;
        set
        {
            if (SetProperty(ref field, value)) _enableVisualContextAnimationGauge.Record(value ? 1 : 0);
        }
    } = true;

    private readonly Gauge<int> _enableVisualElementPickAnimationGauge =
        Meter.CreateGauge<int>($"settings.{nameof(EnableVisualElementPickAnimation)}");
    private readonly Gauge<int> _enableVisualContextAnimationGauge =
        Meter.CreateGauge<int>($"settings.{nameof(EnableVisualContextAnimation)}");

    public ChatWindowSettings()
    {
        // TODO: make these ugly codes better
        _enableVisualElementPickAnimationGauge.Record(EnableVisualElementPickAnimation ? 1 : 0);
        _enableVisualContextAnimationGauge.Record(EnableVisualContextAnimation ? 1 : 0);
    }
}