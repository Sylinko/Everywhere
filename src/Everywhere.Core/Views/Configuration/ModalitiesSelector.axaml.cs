using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Everywhere.AI;

namespace Everywhere.Views;

public sealed class ModalitiesSelector : TemplatedControl
{
    /// <summary>
    /// Defines the <see cref="Modalities"/> property.
    /// </summary>
    public static readonly StyledProperty<Modalities> ModalitiesProperty =
        AvaloniaProperty.Register<ModalitiesSelector, Modalities>(nameof(Modalities), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// Gets or sets the selected modalities.
    /// </summary>
    public Modalities Modalities
    {
        get => GetValue(ModalitiesProperty);
        set => SetValue(ModalitiesProperty, value);
    }

    public static readonly DirectProperty<ModalitiesSelector, bool> SupportsTextProperty =
        AvaloniaProperty.RegisterDirect<ModalitiesSelector, bool>(
        nameof(SupportsText),
        o => o.SupportsText,
        (o, v) => o.SupportsText = v);

    public bool SupportsText
    {
        get => (Modalities & Modalities.Text) != 0;
        set => SetSupportsModalities(Modalities.Text, value);
    }

    public static readonly DirectProperty<ModalitiesSelector, bool> SupportsImageProperty =
        AvaloniaProperty.RegisterDirect<ModalitiesSelector, bool>(
        nameof(SupportsImage),
        o => o.SupportsImage,
        (o, v) => o.SupportsImage = v);

    public bool SupportsImage
    {
        get => (Modalities & Modalities.Image) != 0;
        set => SetSupportsModalities(Modalities.Image, value);
    }

    public static readonly DirectProperty<ModalitiesSelector, bool> SupportsAudioProperty =
        AvaloniaProperty.RegisterDirect<ModalitiesSelector, bool>(
        nameof(SupportsAudio),
        o => o.SupportsAudio,
        (o, v) => o.SupportsAudio = v);

    public bool SupportsAudio
    {
        get => (Modalities & Modalities.Audio) != 0;
        set => SetSupportsModalities(Modalities.Audio, value);
    }

    public static readonly DirectProperty<ModalitiesSelector, bool> SupportsVideoProperty =
        AvaloniaProperty.RegisterDirect<ModalitiesSelector, bool>(
        nameof(SupportsVideo),
        o => o.SupportsVideo,
        (o, v) => o.SupportsVideo = v);

    public bool SupportsVideo
    {
        get => (Modalities & Modalities.Video) != 0;
        set => SetSupportsModalities(Modalities.Video, value);
    }

    public static readonly DirectProperty<ModalitiesSelector, bool> SupportsPdfProperty =
        AvaloniaProperty.RegisterDirect<ModalitiesSelector, bool>(
        nameof(SupportsPdf),
        o => o.SupportsPdf,
        (o, v) => o.SupportsPdf = v);

    public bool SupportsPdf
    {
        get => (Modalities & Modalities.Pdf) != 0;
        set => SetSupportsModalities(Modalities.Pdf, value);
    }

    private void SetSupportsModalities(Modalities modality, bool supports)
    {
        if (supports) Modalities |= modality;
        else Modalities &= ~modality;
    }

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

        void TryRaisePropertyChanged(DirectProperty<ModalitiesSelector, bool> property, Modalities flag)
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