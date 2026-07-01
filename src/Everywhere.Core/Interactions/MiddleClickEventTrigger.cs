using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;

namespace Everywhere.Interactions;

public class MiddleClickEventTrigger : StyledElementTrigger<Control>
{
    private bool _isPressed;

    protected override void OnAttached()
    {
        if (AssociatedObject is { } control)
        {
            control.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            control.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        }

        base.OnAttached();
    }

    protected override void OnDetaching()
    {
        if (AssociatedObject is { } control)
        {
            control.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            control.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        }

        base.OnDetaching();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Properties.IsMiddleButtonPressed)
        {
            _isPressed = true;
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPressed && e.Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased && IsPointerWithinResolvedSource(e))
        {
            _isPressed = false;
            Interaction.ExecuteActions(sender, Actions, e);
            e.Handled = true;
        }
    }

    private bool IsPointerWithinResolvedSource(PointerReleasedEventArgs e)
    {
        if (AssociatedObject is not { } control)
        {
            return false;
        }

        var position = e.GetPosition(control);
        return control.GetVisualsAt(position).Any(visual => ReferenceEquals(visual, control) || control.IsVisualAncestorOf(visual));
    }
}