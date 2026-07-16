using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Everywhere.Views;

public sealed class ConditionalContentControl : Decorator
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Ensure that the control is initialized (especially ensure ContentDataBindingProperty is set before the control is initialized)
        if (!IsInitialized) return;

        if (change.Property == ConditionProperty ||
            change.Property == TrueContentProperty ||
            change.Property == FalseContentProperty ||
            change.Property == NullContentProperty)
        {
            UpdateContent(true);
        }
        else if (change.Property == ContentDataBindingProperty)
        {
            UpdateContent(false);
        }
    }

    protected override void OnInitialized()
    {
        UpdateContent(true);
    }

    private void UpdateContent(bool rebuild)
    {
        var content = Condition switch
        {
            true => TrueContent,
            false => FalseContent,
            _ => NullContent,
        };

        var dataContext = ContentDataBinding ?? DataContext;
        if (content?.Match(dataContext) is not true)
        {
            Child = null;
            return;
        }

        Control? control;
        if (rebuild)
        {
            control = content.Build(this);
            Child = control;
        }
        else
        {
            control = Child;
        }

        control?.DataContext = dataContext;
    }
}