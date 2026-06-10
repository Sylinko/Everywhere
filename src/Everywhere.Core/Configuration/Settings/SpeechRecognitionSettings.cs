using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Media.SpeechRecognition;
using Everywhere.Media.SpeechRecognition.Sherpa;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public sealed partial class SpeechRecognitionSettings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider)
{
    [ObservableProperty]
    [SettingsItemIgnore]
    public partial string? SelectedEngineId { get; set; }

    public SherpaOnnxSpeechRecognitionEngineSettings SherpaOnnx { get; } = new(serviceProvider);
}
