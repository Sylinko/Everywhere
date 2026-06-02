using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Everywhere.Media;

namespace Everywhere.Views;

[TemplatePart(Name = ButtonPartName, Type = typeof(Button), IsRequired = true)]
public sealed class SpeechRecognitionButton : TemplatedControl
{
    private const string ButtonPartName = "PART_Button";

    public static readonly StyledProperty<SpeechRecognitionStatus> StatusProperty =
        AvaloniaProperty.Register<SpeechRecognitionButton, SpeechRecognitionStatus>(nameof(Status));

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<SpeechRecognitionButton, ICommand?>(nameof(Command));

    public SpeechRecognitionStatus Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    private Button? _button;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _button = e.NameScope.Find<Button>(ButtonPartName);
        if (_button is not null && Status.ToString() is { Length: > 0 } newClass) _button.Classes.Add(newClass);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);

        if (_button is not null && args.Property == StatusProperty)
        {
            if (args.OldValue?.ToString() is { Length: > 0 } oldClass) _button.Classes.Remove(oldClass);
            if (args.NewValue?.ToString() is { Length: > 0 } newClass) _button.Classes.Add(newClass);
        }
    }
}

public sealed class RecordingWaveform : Control
{
    private const double BarWidth = 2.0d;
    private const double BarSpacing = 5.0d;
    private const double PixelsPerSecond = 26.0d;

    public static readonly StyledProperty<IBrush?> WaveBrushProperty =
        AvaloniaProperty.Register<RecordingWaveform, IBrush?>(nameof(WaveBrush));

    public IBrush? WaveBrush
    {
        get => GetValue(WaveBrushProperty);
        set => SetValue(WaveBrushProperty, value);
    }

    public static readonly DirectProperty<RecordingWaveform, TimeSpan> TimeProperty =
        AvaloniaProperty.RegisterDirect<RecordingWaveform, TimeSpan>(nameof(Time), o => o.Time);

    public TimeSpan Time
    {
        get;
        private set => SetAndRaise(TimeProperty, ref field, value);
    }

    private TopLevel? _topLevel;
    private TimeSpan? _startedAt;

    static RecordingWaveform()
    {
        AffectsRender<RecordingWaveform>(TimeProperty, WaveBrushProperty);
    }

    public RecordingWaveform()
    {
        IsHitTestVisible = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.RequestAnimationFrame(HandleAnimationFrame);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel = null;

        base.OnDetachedFromVisualTree(e);
    }

    private void HandleAnimationFrame(TimeSpan timestamp)
    {
        if (_startedAt is null)
        {
            Time = TimeSpan.Zero;
            _startedAt  = timestamp;
        }
        else
        {
            Time = timestamp - _startedAt.Value;
        }

        _topLevel?.RequestAnimationFrame(HandleAnimationFrame);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var brush = WaveBrush;
        if (brush == null) return;

        var distance = Time.TotalSeconds * PixelsPerSecond;
        var newestSlot = (int)Math.Floor(distance / BarSpacing);
        var oldestVisibleSlot = Math.Max(0, (int)Math.Floor((distance - width - BarWidth) / BarSpacing));
        var minHeight = Math.Max(3.0d, height * 0.18d);
        var maxHeight = Math.Max(minHeight, height * 0.82d);
        var centerY = height / 2.0d;

        for (var slot = oldestVisibleSlot; slot <= newestSlot; slot++)
        {
            var x = width - (distance - slot * BarSpacing);
            if (x < -BarWidth || x > width + BarWidth) continue;

            var normalized = HashToUnit(slot);
            var barHeight = minHeight + normalized * (maxHeight - minHeight);
            var rect = new Rect(x, centerY - barHeight / 2.0d, BarWidth, barHeight);
            context.DrawRectangle(brush, null, new RoundedRect(rect, BarWidth / 2.0d));
        }
    }

    private static double HashToUnit(int value)
    {
        unchecked
        {
            var hash = (uint)value;
            hash ^= hash >> 16;
            hash *= 0x7feb352d;
            hash ^= hash >> 15;
            hash *= 0x846ca68b;
            hash ^= hash >> 16;
            return hash / (double)uint.MaxValue;
        }
    }
}
