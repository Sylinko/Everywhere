using Avalonia.Controls;

namespace Everywhere.Views;

public class ChatMessageControl : ContentControl
{
    /// <summary>
    /// Defines the <see cref="IsLast"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsLastProperty =
        AvaloniaProperty.Register<ChatMessageControl, bool>(nameof(IsLast));

    /// <summary>
    /// Gets or sets a value indicating whether this chat message is the last message in the chat history.
    /// This can be used to control the visibility of certain UI elements, such as a "Continue" button for assistant messages.
    /// </summary>
    public bool IsLast
    {
        get => GetValue(IsLastProperty);
        set => SetValue(IsLastProperty, value);
    }
}