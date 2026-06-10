using Avalonia.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;
using Everywhere.Media.Views;
using Everywhere.Media.Views.Sherpa;

namespace Everywhere.Media.SpeechRecognition.Sherpa;

[GeneratedSettingsItems]
public sealed partial class SherpaOnnxSpeechRecognitionEngineSettings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider)
{
    [ObservableProperty]
    [SettingsItemIgnore]
    public partial string? MicrophoneDeviceId { get; set; }

    [DynamicResourceKey(
        LocaleKey.SherpaOnnxSpeechRecognitionEngineSettings_MicrophoneDeviceId_Header,
        LocaleKey.SherpaOnnxSpeechRecognitionEngineSettings_MicrophoneDeviceId_Description)]
    public SettingsControl<MicrophoneDeviceComboBox> MicrophoneDeviceIdSettingsControl => new(x => new MicrophoneDeviceComboBox(x)
    {
        [!MicrophoneDeviceComboBox.SelectedDeviceIdProperty] = new Binding(nameof(MicrophoneDeviceId))
        {
            Source = this,
            Mode = BindingMode.TwoWay
        }
    });

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial string? ModelId { get; set; }

    [DynamicResourceKey(
        LocaleKey.SherpaOnnxSpeechRecognitionEngineSettings_ModelId_Header,
        LocaleKey.SherpaOnnxSpeechRecognitionEngineSettings_ModelId_Description)]
    public SettingsControl<SherpaOnnxModelSelector> ModelIdSettingsControl => new(x => new SherpaOnnxModelSelector(x)
    {
        [!SherpaOnnxModelSelector.SelectedModelIdProperty] = new Binding(nameof(ModelId))
        {
            Source = this,
            Mode = BindingMode.TwoWay
        }
    });

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.SherpaOnnxSpeechRecognitionEngineSettings_ThreadCount_Header,
        LocaleKey.SherpaOnnxSpeechRecognitionEngineSettings_ThreadCount_Description)]
    [SettingsIntegerItem(Min = 0, Max = 16, IsSliderVisible = true, IsTextBoxVisible = true)]
    public partial int ThreadCount { get; set; } = 2;
}