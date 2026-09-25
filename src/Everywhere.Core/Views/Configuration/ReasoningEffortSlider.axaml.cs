using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Everywhere.AI;
using Everywhere.Utilities;

namespace Everywhere.Views;

/// <summary>
/// Binds a discrete Slider to protocol options containing user-ordered reasoning values.
/// The template keeps native input separate from animated decoration; motion is implemented in the companion partial.
/// </summary>
public sealed partial class ReasoningEffortSlider : Slider
{
    public static readonly StyledProperty<ReasoningModelSchemaOptions?> OptionsProperty =
        AvaloniaProperty.Register<ReasoningEffortSlider, ReasoningModelSchemaOptions?>(nameof(Options));

    public ReasoningModelSchemaOptions? Options
    {
        get => GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public static readonly DirectProperty<ReasoningEffortSlider, bool> HasMultipleValuesProperty =
        AvaloniaProperty.RegisterDirect<ReasoningEffortSlider, bool>(nameof(HasMultipleValues), x => x.HasMultipleValues);

    public bool HasMultipleValues
    {
        get;
        private set => SetAndRaise(HasMultipleValuesProperty, ref field, value);
    }

    protected override Type StyleKeyOverride => typeof(ReasoningEffortSlider);

    private const double NodeDiameter = 12;

    private ReasoningModelSchemaOptions? _subscribedOptions;
    private IDisposable? _thumbSubscription;
    private string[] _choices = [];
    private Canvas? _nodes;
    private Control? _indicator;
    private Control? _line;
    private bool _isAttached;
    private bool _isSynchronizing;

    public ReasoningEffortSlider()
    {
        Minimum = 0;
        TickFrequency = SmallChange = LargeChange = 1;
        IsSnapToTickEnabled = true;
        ClipToBounds = false;
        InitializeMotion();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        ResetMotion();
        DisposeHelper.DisposeToDefault(ref _thumbSubscription);

        base.OnApplyTemplate(e);

        _nodes = e.NameScope.Find<Canvas>("PART_Nodes");

        _indicator = e.NameScope.Find<Control>("PART_Indicator");
        _indicator?.RenderTransform = _indicatorTranslation;

        BindThumbStyling(e);

        _line = e.NameScope.Find<Control>("PART_Line");
        _line?.RenderTransform = _lineTransform;
        RebuildNodes();
    }

    private void BindThumbStyling(TemplateAppliedEventArgs e)
    {
        var thumb = e.NameScope.Find<Thumb>("PART_Thumb");
        if (thumb is null) return;

        var indicatorThumb = e.NameScope.Find<Control>("PART_IndicatorThumb");
        if (indicatorThumb is null) return;

        IPseudoClasses targetStyles = indicatorThumb.Classes;
        var subscriptions = new CompositeDisposable();
        _thumbSubscription = subscriptions;
        subscriptions.Add(thumb.AddDisposableHandler(PointerEnteredEvent, (_, _) => targetStyles.Add(":pointerover"), handledEventsToo: true));
        subscriptions.Add(thumb.AddDisposableHandler(PointerExitedEvent, (_, _) => targetStyles.Remove(":pointerover"), handledEventsToo: true));
        subscriptions.Add(thumb.AddDisposableHandler(PointerPressedEvent, (_, _) => targetStyles.Add(":pressed"), handledEventsToo: true));
        subscriptions.Add(thumb.AddDisposableHandler(PointerReleasedEvent, (_, _) => targetStyles.Remove(":pressed"), handledEventsToo: true));
        subscriptions.Add(thumb.AddDisposableHandler(PointerCaptureLostEvent, (_, _) => targetStyles.Remove(":pressed"), handledEventsToo: true));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        Unsubscribe();
        ResetMotion();
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        // Thumb geometry is only reliable after Track has arranged. Derive endpoints from the actual
        // input template instead of duplicating its hit-target width in the positioning arithmetic.
        UpdateTrackGeometry();
        return result;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OptionsProperty)
        {
            ResetMotion();
            Subscribe();
        }
        else if (change.Property == ValueProperty && !_isSynchronizing)
        {
            if (Options is { } options && _choices.Length > 0)
            {
                var index = Math.Clamp((int)Math.Round(Value), 0, _choices.Length - 1);
                options.SelectedReasoningEffort = _choices[index];
            }
            MoveIndicator(animate: true);
        }
        else if (change.Property == IndicatorPositionProperty || change.Property == EdgeStretchProperty)
        {
            UpdateDecoration();
        }
        else if (change.Property == IsDirectionReversedProperty)
        {
            ResetMotion();
            UpdateDecoration();
        }
        else if (change.Property == IsEnabledProperty && !IsEnabled || change.Property == IsVisibleProperty && !IsVisible)
        {
            ResetMotion();
        }
        else if (change.Property == BackgroundProperty || change.Property == BorderBrushProperty)
        {
            UpdateNodeBrushes();
        }
    }

    private void Subscribe()
    {
        Unsubscribe();
        if (_isAttached && Options is { } options)
        {
            _subscribedOptions = options;
            options.PropertyChanged += HandleModelChanged;
        }
        RefreshChoices();
    }

    private void Unsubscribe()
    {
        _subscribedOptions?.PropertyChanged -= HandleModelChanged;
        _subscribedOptions = null;
    }

    private void HandleModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is
            nameof(ReasoningModelSchemaOptions.ReasoningEffortValues) or
            nameof(ReasoningModelSchemaOptions.EffectiveReasoningEffort))
        {
            // Model notifications are the only background ingress. All gesture and animation state is UI-owned.
            Dispatcher.UIThread.PostOnDemand(() =>
            {
                if (_isAttached && !_isSynchronizing) RefreshChoices();
            });
        }
    }

    private void RefreshChoices()
    {
        var options = Options;
        var choices = options?.ReasoningEffortValues.ToArray() ?? [];
        var changed = !_choices.SequenceEqual(choices);
        _isSynchronizing = true;
        try
        {
            _choices = choices;
            HasMultipleValues = choices.Length > 1;
            Maximum = Math.Max(0, choices.Length - 1);
            SetCurrentValue(ValueProperty, Math.Max(0, Array.IndexOf(choices, options?.EffectiveReasoningEffort)));
            if (changed)
            {
                ResetMotion();
                RebuildNodes();
            }
            MoveIndicator(animate: !changed);
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void RebuildNodes()
    {
        if (_nodes is null) return;
        // Selection and animation never rebuild nodes. Even edits can reuse them when the count is unchanged.
        while (_nodes.Children.Count > _choices.Length) _nodes.Children.RemoveAt(_nodes.Children.Count - 1);
        while (_nodes.Children.Count < _choices.Length)
        {
            _nodes.Children.Add(
                new Ellipse
                {
                    Width = NodeDiameter,
                    Height = NodeDiameter,
                    StrokeThickness = 1,
                    Fill = Background,
                    Stroke = BorderBrush,
                    RenderTransform = new TranslateTransform()
                });
        }
        UpdateDecoration();
    }

    private void UpdateNodeBrushes()
    {
        if (_nodes is null) return;
        foreach (var node in _nodes.Children.OfType<Ellipse>())
        {
            node.Fill = Background;
            node.Stroke = BorderBrush;
        }
    }
}
