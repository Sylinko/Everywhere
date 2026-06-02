using System.ComponentModel;
using Everywhere.Collections;
using Everywhere.Media.SpeechRecognition;

namespace Everywhere.Media;

public interface ISpeechRecognitionService : INotifyPropertyChanged
{
    bool IsAvailable { get; }

    SpeechRecognitionStatus Status { get; }

    IReadOnlyBindableList<ISpeechRecognitionEngine> Engines { get; }

    ISpeechRecognitionEngine? SelectedEngine { get; set; }

    SpeechRecognitionInputState? TryCreateInputState(SpeechRecognitionActivationKind activationKind);

    Task StartSpeechRecognitionAsync(
        SpeechRecognitionInputState state,
        LocaleName? locale = null,
        CancellationToken cancellationToken = default);

    Task StopSpeechRecognitionAsync(
        SpeechRecognitionInputState state,
        CancellationToken cancellationToken = default);
}
