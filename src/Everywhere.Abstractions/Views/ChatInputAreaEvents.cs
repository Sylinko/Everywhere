using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Everywhere.Views;

public sealed class PreeditChangedEventArgs(RoutedEvent routedEvent, string? preeditText, Rect cursorRectangle) : RoutedEventArgs(routedEvent)
{
    public string? PreeditText { get; } = preeditText;

    public Rect CursorRectangle { get; } = cursorRectangle;
}

public static class PreeditChangedEventRegistry
{
    public static readonly RoutedEvent<PreeditChangedEventArgs> PreeditChangedEvent =
        RoutedEvent.Register<Control, PreeditChangedEventArgs>("PreeditChanged", RoutingStrategies.Bubble);
}
