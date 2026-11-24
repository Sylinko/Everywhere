using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;

namespace Everywhere.Views;

[PseudoClasses(":cancellable", ":interactive")]
public sealed class ActivityMarker : Expander
{
    public static readonly StyledProperty<object?> IconProperty =
        AvaloniaProperty.Register<ActivityMarker, object?>(nameof(Icon));

    public object? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public static readonly StyledProperty<object?> SecondaryHeaderProperty =
        AvaloniaProperty.Register<ActivityMarker, object?>(nameof(SecondaryHeader));

    public object? SecondaryHeader
    {
        get => GetValue(SecondaryHeaderProperty);
        set => SetValue(SecondaryHeaderProperty, value);
    }

    public static readonly StyledProperty<bool> IsSecondaryHeaderVisibleProperty =
        AvaloniaProperty.Register<ActivityMarker, bool>(nameof(IsSecondaryHeaderVisible), true);

    public bool IsSecondaryHeaderVisible
    {
        get => GetValue(IsSecondaryHeaderVisibleProperty);
        set => SetValue(IsSecondaryHeaderVisibleProperty, value);
    }

    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<ActivityMarker, bool>(nameof(IsRunning));

    public bool IsRunning
    {
        get => GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    public static readonly StyledProperty<bool> IsExpandableProperty =
        AvaloniaProperty.Register<ActivityMarker, bool>(nameof(IsExpandable), true);

    public bool IsExpandable
    {
        get => GetValue(IsExpandableProperty);
        set => SetValue(IsExpandableProperty, value);
    }

    public static readonly StyledProperty<double> HorizontalSpacingProperty =
        AvaloniaProperty.Register<ActivityMarker, double>(nameof(HorizontalSpacing));

    public double HorizontalSpacing
    {
        get => GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<ActivityMarker, ICommand?>(nameof(CancelCommand));

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public static readonly StyledProperty<object?> CancelCommandParameterProperty =
        AvaloniaProperty.Register<ActivityMarker, object?>(nameof(CancelCommandParameter));

    public object? CancelCommandParameter
    {
        get => GetValue(CancelCommandParameterProperty);
        set => SetValue(CancelCommandParameterProperty, value);
    }

    public static readonly StyledProperty<bool> IsCancelEnabledProperty =
        AvaloniaProperty.Register<ActivityMarker, bool>(nameof(IsCancelEnabled), true);

    public bool IsCancelEnabled
    {
        get => GetValue(IsCancelEnabledProperty);
        set => SetValue(IsCancelEnabledProperty, value);
    }

    public static readonly DirectProperty<ActivityMarker, bool> CanCancelProperty =
        AvaloniaProperty.RegisterDirect<ActivityMarker, bool>(nameof(CanCancel), static o => o.CanCancel);

    public bool CanCancel
    {
        get;
        private set => SetAndRaise(CanCancelProperty, ref field, value);
    }

    private bool _isAttachedToVisualTree;

    public ActivityMarker()
    {
        UpdateInteractionState();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _isAttachedToVisualTree = true;
        if (CancelCommand is { } command)
        {
            command.CanExecuteChanged -= HandleCancelCommandCanExecuteChanged;
            command.CanExecuteChanged += HandleCancelCommandCanExecuteChanged;
        }

        UpdateInteractionState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _isAttachedToVisualTree = false;
        if (CancelCommand is { } command)
        {
            command.CanExecuteChanged -= HandleCancelCommandCanExecuteChanged;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CancelCommandProperty)
        {
            if (_isAttachedToVisualTree)
            {
                if (change.GetOldValue<ICommand?>() is { } oldCommand)
                {
                    oldCommand.CanExecuteChanged -= HandleCancelCommandCanExecuteChanged;
                }

                if (change.GetNewValue<ICommand?>() is { } newCommand)
                {
                    newCommand.CanExecuteChanged -= HandleCancelCommandCanExecuteChanged;
                    newCommand.CanExecuteChanged += HandleCancelCommandCanExecuteChanged;
                }
            }

            UpdateInteractionState();
        }
        else if (change.Property == CancelCommandParameterProperty ||
                 change.Property == IsRunningProperty ||
                 change.Property == IsCancelEnabledProperty ||
                 change.Property == IsExpandableProperty)
        {
            UpdateInteractionState();
        }
    }

    private void HandleCancelCommandCanExecuteChanged(object? sender, EventArgs e) => UpdateInteractionState();

    private void UpdateInteractionState()
    {
        var canCancel = IsRunning &&
            IsCancelEnabled &&
            CancelCommand is { } command &&
            command.CanExecute(CancelCommandParameter);
        CanCancel = canCancel;
        PseudoClasses.Set(":cancellable", canCancel);
        PseudoClasses.Set(":interactive", IsExpandable || canCancel);
    }
}