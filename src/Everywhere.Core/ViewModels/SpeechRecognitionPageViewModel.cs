using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Configuration;
using Everywhere.Media;
using Everywhere.Media.SpeechRecognition;

namespace Everywhere.ViewModels;

public sealed partial class SpeechRecognitionPageViewModel(
    Settings settings,
    ISpeechRecognitionService speechRecognitionService
) : ReactiveViewModelBase
{
    public SpeechRecognitionSettings SpeechRecognitionSettings => settings.SpeechRecognition;

    public ISpeechRecognitionService SpeechRecognitionService => speechRecognitionService;

    /// <summary>
    /// Different with SpeechRecognitionService.SelectedEngine, this property is the View selected engine, which can be used to set the default engine.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsItems))]
    [NotifyCanExecuteChangedFor(nameof(SetDefaultCommand))]
    public partial ISpeechRecognitionEngine? SelectedSpeechRecognitionEngine { get; set; }

    public IEnumerable<SettingsItem>? SettingsItems => SelectedSpeechRecognitionEngine.As<IHaveSettingsItems>()?.SettingsItems;

    public bool CanSetDefault =>
        SelectedSpeechRecognitionEngine is not null &&
        SelectedSpeechRecognitionEngine.Id != speechRecognitionService.SelectedEngine?.Id;

    [RelayCommand(CanExecute = nameof(CanSetDefault))]
    private void SetDefault()
    {
        if (SelectedSpeechRecognitionEngine is null || !SelectedSpeechRecognitionEngine.IsSupported)
        {
            return;
        }

        speechRecognitionService.SelectedEngine = SelectedSpeechRecognitionEngine;
        SetDefaultCommand.NotifyCanExecuteChanged();
    }
}
