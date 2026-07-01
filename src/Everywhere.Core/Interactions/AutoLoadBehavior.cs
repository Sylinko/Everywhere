using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using Everywhere.Common;
using Everywhere.Utilities;

namespace Everywhere.Interactions;

public class AutoLoadBehavior : Behavior<Control>
{
    /// <summary>
    /// Defines the threshold (in pixels) from the bottom of the ScrollViewer at which the Command will be executed.
    /// </summary>
    public static readonly StyledProperty<double> ThresholdProperty =
        AvaloniaProperty.Register<AutoLoadBehavior, double>(nameof(Threshold), 8.0);

    /// <summary>
    /// Gets or sets the threshold (in pixels) from the bottom of the ScrollViewer at which the Command will be executed.
    /// </summary>
    public double Threshold
    {
        get => GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    public static readonly StyledProperty<TimeSpan> DebounceProperty =
        AvaloniaProperty.Register<AutoLoadBehavior, TimeSpan>(nameof(Debounce), TimeSpan.FromSeconds(0.5));

    public TimeSpan Debounce
    {
        get => GetValue(DebounceProperty);
        set => SetValue(DebounceProperty, value);
    }

    /// <summary>
    /// Defines the command to be executed when the ScrollViewer is scrolled within the specified Threshold from the bottom.
    /// </summary>
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<AutoLoadBehavior, ICommand?>(nameof(Command));

    /// <summary>
    /// Gets or sets the command to be executed when the ScrollViewer is scrolled within the specified Threshold from the bottom.
    /// </summary>
    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    /// <summary>
    /// Defines the command parameter to be passed to the Command when executed.
    /// </summary>
    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<AutoLoadBehavior, object?>(nameof(CommandParameter));

    /// <summary>
    /// Gets or sets the command parameter to be passed to the Command when executed.
    /// </summary>
    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    private bool _isAtEnd;
    private double _lastExtentHeight;
    private double _lastViewportHeight;
    private readonly DebounceExecutor<AutoLoadBehavior, DispatcherTimerImpl> _debounceExecutor;

    public AutoLoadBehavior()
    {
        _debounceExecutor = new DebounceExecutor<AutoLoadBehavior, DispatcherTimerImpl>(
            () => this,
            static that =>
            {
                var command = that.Command;
                var parameter = that.CommandParameter;
                if (command?.CanExecute(parameter) is not true) return;
                command.Execute(parameter);
            },
            Debounce);
    }

    protected override void OnAttached()
    {
        base.OnAttached();

        switch (AssociatedObject)
        {
            case ScrollViewer scrollViewer:
            {
                scrollViewer.ScrollChanged += HandleScrollViewer;
                break;
            }
            case ListBox listBox:
            {
                if (listBox.Scroll is ScrollViewer scrollViewer)
                {
                    scrollViewer.ScrollChanged += HandleScrollViewer;
                }

                listBox.PropertyChanged += HandleListBox;
                break;
            }
            case DataGrid dataGrid:
            {
                dataGrid.TemplateApplied += HandleDataGrid;
                break;
            }
        }
    }

    private void HandleScrollViewer(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer) return;
        HandleScroll(scrollViewer.Extent.Height, scrollViewer.Viewport.Height, scrollViewer.Offset.Y);
    }

    private void HandleListBox(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ListBox.ScrollProperty) return;
        if (e.OldValue is ScrollViewer oldScrollViewer) oldScrollViewer.ScrollChanged -= HandleScrollViewer;
        if (e.NewValue is not ScrollViewer scrollViewer) return;
        scrollViewer.ScrollChanged += HandleScrollViewer;
    }

    private void HandleDataGrid(object? sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid dataGrid) return;
        if (dataGrid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scrollViewer) return;
        scrollViewer.ScrollChanged += HandleScrollViewer;
    }

    private void HandleScroll(double extentHeight, double viewportHeight, double offsetY)
    {
        // Reset _isAtEnd when content size or viewport size changes (e.g. new items loaded, window resized),
        // so the trigger re-evaluates even if we were already at the end.
        if (_isAtEnd && (Math.Abs(extentHeight - _lastExtentHeight) > 0.01 || Math.Abs(viewportHeight - _lastViewportHeight) > 0.01))
        {
            _isAtEnd = false;
        }

        _lastExtentHeight = extentHeight;
        _lastViewportHeight = viewportHeight;

        if (extentHeight - (offsetY + viewportHeight) <= Threshold)
        {
            if (_isAtEnd) return;
            _debounceExecutor.Trigger();
            _isAtEnd = true;
        }
        else
        {
            _isAtEnd = false;
        }
    }
}