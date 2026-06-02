using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Media;

public sealed partial class SpeechRecognitionInputState : ObservableObject
{
    internal SpeechRecognitionInputState(SpeechRecognitionActivationKind activationKind)
    {
        ActivationKind = activationKind;
        ActivationId = Guid.NewGuid();
    }

    public SpeechRecognitionActivationKind ActivationKind { get; }

    public Guid ActivationId { get; }

    [ObservableProperty]
    public partial string? Composition { get; internal set; }

    [ObservableProperty]
    public partial Exception? LastException { get; internal set; }

    [ObservableProperty]
    public partial bool IsActive { get; internal set; } = true;

    public event EventHandler<string>? CommitRequested;

    public event EventHandler? CompositionResetRequested;

    internal void RequestCommit(string text) => CommitRequested?.Invoke(this, text);

    internal void RequestCompositionReset() => CompositionResetRequested?.Invoke(this, EventArgs.Empty);
}
