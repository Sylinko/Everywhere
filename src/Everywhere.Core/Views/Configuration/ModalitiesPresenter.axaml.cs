using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Everywhere.AI;

namespace Everywhere.Views;

public sealed class ModalitiesPresenter : TemplatedControl
{
    /// <summary>
    /// Defines the <see cref="Modalities"/> property.
    /// </summary>
    public static readonly StyledProperty<Modalities> ModalitiesProperty =
        AvaloniaProperty.Register<ModalitiesPresenter, Modalities>(nameof(Modalities));

    /// <summary>
    /// Gets or sets the selected modalities.
    /// </summary>
    public Modalities Modalities
    {
        get => GetValue(ModalitiesProperty);
        set => SetValue(ModalitiesProperty, value);
    }

    public static readonly DirectProperty<ModalitiesPresenter, bool> SupportsTextProperty =
        AvaloniaProperty.RegisterDirect<ModalitiesPresenter, bool>(
        nameof(SupportsText),
        o => o.SupportsText);

    public bool SupportsText => (Modalities & Modalities.Text) != 0;

    public static readonly DirectProperty<ModalitiesPresenter, bool> SupportsImageProperty =
        AvaloniaProperty.RegisterDirect<ModalitiesPresenter, bool>(
        nameof(SupportsImage),
        o => o.SupportsImage);

    public bool SupportsImage => (Modalities & Modalities.Image) != 0;

    public static readonly DirectProperty<ModalitiesPresenter, bool> SupportsAudioProperty =
        AvaloniaProperty.RegisterDirect<ModalitiesPresenter, bool>(
        nameof(SupportsAudio),
        o => o.SupportsAudio);

    public bool SupportsAudio => (Modalities & Modalities.Audio) != 0;

    public static readonly DirectProperty<ModalitiesPresenter, bool> SupportsVideoProperty =
        AvaloniaProperty.RegisterDirect<ModalitiesPresenter, bool>(
        nameof(SupportsVideo),
        o => o.SupportsVideo);

    public bool SupportsVideo => (Modalities & Modalities.Video) != 0;

    public static readonly DirectProperty<ModalitiesPresenter, bool> SupportsPdfProperty =
        AvaloniaProperty.RegisterDirect<ModalitiesPresenter, bool>(
        nameof(SupportsPdf),
        o => o.SupportsPdf);

    public bool SupportsPdf => (Modalities & Modalities.Pdf) != 0;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != ModalitiesProperty) return;

        var oldValue = (Modalities)change.OldValue!;
        var newValue = (Modalities)change.NewValue!;

        // Update the individual modality properties when Modalities changes
        TryRaisePropertyChanged(SupportsTextProperty, Modalities.Text);
        TryRaisePropertyChanged(SupportsImageProperty, Modalities.Image);
        TryRaisePropertyChanged(SupportsAudioProperty, Modalities.Audio);
        TryRaisePropertyChanged(SupportsVideoProperty, Modalities.Video);
        TryRaisePropertyChanged(SupportsPdfProperty, Modalities.Pdf);

        void TryRaisePropertyChanged(DirectProperty<ModalitiesPresenter, bool> property, Modalities flag)
        {
            var oldSupports = (oldValue & flag) != 0;
            var newSupports = (newValue & flag) != 0;
            if (oldSupports != newSupports)
            {
                RaisePropertyChanged(property, oldSupports, newSupports);
            }
        }
    }
}