using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Configuration;

public sealed partial class SpeechRecognitionSettings : SettingsBase
{
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.SpeechRecognitionSettings_IsEnabled_Header,
        LocaleKey.SpeechRecognitionSettings_IsEnabled_Description)]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial string? SelectedEngineId { get; set; }
}
