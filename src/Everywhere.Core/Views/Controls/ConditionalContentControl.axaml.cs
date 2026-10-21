using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Everywhere.Views;

public sealed class ConditionalContentControl : TransitioningContentControl
{
    /// <summary>
    /// Defines the <see cref="Condition"/> property.
    /// </summary>
    public static readonly StyledProperty<bool?> ConditionProperty =
        AvaloniaProperty.Register<ConditionalContentControl, bool?>(nameof(Condition));

    /// <summary>
    /// Gets or sets the condition that determines which content should be displayed.
    /// </summary>
    public bool? Condition
    {
        get => GetValue(ConditionProperty);
        set => SetValue(ConditionProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="TrueContent"/> property.
    /// </summary>
    public static readonly StyledProperty<IDataTemplate?> TrueContentProperty =
        AvaloniaProperty.Register<ConditionalContentControl, IDataTemplate?>(nameof(TrueContent));

    /// <summary>
    /// Gets or sets the content template to display when <see cref="Condition"/> is true.
    /// </summary>
    public IDataTemplate? TrueContent
    {
        get => GetValue(TrueContentProperty);
        set => SetValue(TrueContentProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="FalseContent"/> property.
    /// </summary>
    public static readonly StyledProperty<IDataTemplate?> FalseContentProperty =
        AvaloniaProperty.Register<ConditionalContentControl, IDataTemplate?>(nameof(FalseContent));

    /// <summary>
    /// Gets or sets the content template to display when <see cref="Condition"/> is false.
    /// </summary>
    public IDataTemplate? FalseContent
    {
        get => GetValue(FalseContentProperty);
        set => SetValue(FalseContentProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="NullContent"/> property.
    /// </summary>
    public static readonly StyledProperty<IDataTemplate?> NullContentProperty =
        AvaloniaProperty.Register<ConditionalContentControl, IDataTemplate?>(nameof(NullContent));

    /// <summary>
    /// Gets or sets the content template to display when <see cref="Condition"/> is null.
    /// </summary>
    public IDataTemplate? NullContent
    {
        get => GetValue(NullContentProperty);
        set => SetValue(NullContentProperty, value);
    }

    /// <summary>
    /// Identifies the <see cref="ContentDataBinding"/> property.
    /// </summary>
    public static readonly StyledProperty<object?> ContentDataBindingProperty =
        AvaloniaProperty.Register<ConditionalContentControl, object?>(nameof(ContentDataBinding));

    /// <summary>
    /// Gets or sets the data context for the content of this control.
    /// If not set, the control's own DataContext is used.
    /// </summary>
    public object? ContentDataBinding
    {
        get => GetValue(ContentDataBindingProperty);
        set => SetValue(ContentDataBindingProperty, value);
    }

    /// <summary>
    /// Defines whether the content is cached and reused when switching between true/false/null conditions.
    /// </summary>
    public static readonly StyledProperty<bool> IsCachedProperty =
        AvaloniaProperty.Register<ConditionalContentControl, bool>(nameof(IsCached));

    /// <summary>
    /// Gets or sets whether the content is cached and reused when switching between true/false/null conditions.
    /// </summary>
    public bool IsCached
    {
        get => GetValue(IsCachedProperty);
        set => SetValue(IsCachedProperty, value);
    }

    /// <summary>
    /// Defines whether the content viewport smoothly follows the incoming page's size.
    /// </summary>
    public static readonly StyledProperty<bool> AnimateContentSizeProperty =
        AvaloniaProperty.Register<ConditionalContentControl, bool>(nameof(AnimateContentSize));

    public bool AnimateContentSize
    {
        get => GetValue(AnimateContentSizeProperty);
        set => SetValue(AnimateContentSizeProperty, value);
    }

    /// <summary>
    /// Defines the duration of the optional content size transition, independently of PageTransition.
    /// </summary>
    public static readonly StyledProperty<TimeSpan> ContentSizeTransitionDurationProperty =
        AvaloniaProperty.Register<ConditionalContentControl, TimeSpan>(nameof(ContentSizeTransitionDuration), TimeSpan.FromMilliseconds(400));

    public TimeSpan ContentSizeTransitionDuration
    {
        get => GetValue(ContentSizeTransitionDurationProperty);
        set => SetValue(ContentSizeTransitionDurationProperty, value);
    }

    /// <summary>
    /// Defines the easing used by the optional content size transition.
    /// </summary>
    public static readonly StyledProperty<Easing> ContentSizeTransitionEasingProperty =
        AvaloniaProperty.Register<ConditionalContentControl, Easing>(nameof(ContentSizeTransitionEasing), new CubicEaseInOut());

    public Easing ContentSizeTransitionEasing
    {
        get => GetValue(ContentSizeTransitionEasingProperty);
        set => SetValue(ContentSizeTransitionEasingProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ConditionalContentControl);

    private bool _isContentDirty = true;
    private IDataTemplate? _appliedTemplate;
    private Control?[]? _cachedControls;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (_cachedControls is { } cachedControls)
        {
            if (change.Property == TrueContentProperty)
            {
                cachedControls[0] = null;
            }
            else if (change.Property == FalseContentProperty)
            {
                cachedControls[1] = null;
            }
            else if (change.Property == NullContentProperty)
            {
                cachedControls[2] = null;
            }
            else if (change.Property == IsCachedProperty && !IsCached)
            {
                _cachedControls = null;
            }
        }

        if (change.Property == ConditionProperty ||
            change.Property == TrueContentProperty ||
            change.Property == FalseContentProperty ||
            change.Property == NullContentProperty ||
            change.Property == ContentDataBindingProperty ||
            change.Property == IsCachedProperty)
        {
            InvalidateContent();
        }
    }

    /// <summary>
    /// Applies the current branch synchronously, cancelling any running or pending page transition.
    /// Cached controls remain reusable; the outgoing presenter releases its content immediately.
    /// </summary>
    public void UpdateContentWithoutTransition()
    {
        VerifyAccess();
        ApplyStyling();

        // PageTransition = null only disables new transitions. Cancel the base control's own
        // token as well, so its asynchronous completion cannot hide a reused presenter later.
        CurrentTransition(this)?.Cancel();
        CurrentTransition(this) = null;
        ShouldAnimate(this) = false;

        var transition = PageTransition;
        SetCurrentValue(PageTransitionProperty, null);
        try
        {
            _isContentDirty = false;
            UpdateContent();
            HideOldPresenter(this);
            InvalidateMeasure();
        }
        finally
        {
            SetCurrentValue(PageTransitionProperty, transition);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        InvalidateContent();
    }

    protected override Size MeasureCore(Size availableSize)
    {
        if (IsVisible)
        {
            // Resolve styled inputs before the base implementation applies the control template.
            ApplyStyling();

            // Flyout positioning can measure before initialization or attachment. Prepare content
            // during that premeasure as well, before the base implementation applies the template.
            if (_isContentDirty)
            {
                // Coalesce input changes before committing content for the next layout pass.
                _isContentDirty = false;
                UpdateContent();
            }
        }

        return base.MeasureCore(availableSize);
    }

    private void InvalidateContent()
    {
        _isContentDirty = true;
        InvalidateMeasure();
    }

    private void UpdateContent()
    {
        var condition = Condition;
        var (cacheIndex, template) = condition switch
        {
            true => (0, TrueContent),
            false => (1, FalseContent),
            _ => (2, NullContent),
        };

        var dataContext = ContentDataBinding ?? DataContext;
        if (template is null || !template.Match(dataContext))
        {
            _appliedTemplate = null;
            Content = null;
            return;
        }

        // Template identity determines reuse, even when different branches share the same template.
        if (ReferenceEquals(template, _appliedTemplate) && Content is Control existingControl)
        {
            existingControl.DataContext = dataContext;
            if (IsCached)
            {
                (_cachedControls ??= new Control[3])[cacheIndex] = existingControl;
            }

            return;
        }

        var control = IsCached ?
            (_cachedControls ??= new Control[3])[cacheIndex] ??= template.Build(dataContext) :
            template.Build(dataContext);

        // Set DataContext before attaching the control so it cannot inherit an incorrect value.
        control?.DataContext = dataContext;

        _appliedTemplate = template;
        Content = control;
    }

    // Avalonia 12.1.3 has no public cancellation API. Keep these version-sensitive accesses
    // together rather than duplicating TransitioningContentControl's presenter lifecycle.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_currentTransition")]
    private static extern ref CancellationTokenSource? CurrentTransition(TransitioningContentControl control);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_shouldAnimate")]
    private static extern ref bool ShouldAnimate(TransitioningContentControl control);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "HideOldPresenter")]
    private static extern void HideOldPresenter(TransitioningContentControl control);
}