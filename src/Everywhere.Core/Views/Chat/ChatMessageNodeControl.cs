using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Everywhere.Chat;

namespace Everywhere.Views;

/// <summary>
/// Adapts a <see cref="ChatMessageNode"/> to the message-oriented <see cref="ChatMessageControl"/>
/// while preserving the outer control's inherited data context.
/// </summary>
public sealed class ChatMessageNodeControl : Decorator
{
    public static readonly StyledProperty<ChatMessageNode?> NodeProperty =
        AvaloniaProperty.Register<ChatMessageNodeControl, ChatMessageNode?>(nameof(Node));

    public ChatMessageNode? Node
    {
        get => GetValue(NodeProperty);
        set => SetValue(NodeProperty, value);
    }

    private static readonly IValueConverter NodeToMessageConverter = new FuncValueConverter<ChatMessageNode?, ChatMessage?>(node => node?.Message);

    public ChatMessageNodeControl()
    {
        Child = new ChatMessageControl
        {
            [!ContentControl.ContentProperty] = CompiledBinding.Create(
                expression: (ChatMessageNodeControl x) => x.Node,
                source: this,
                converter: NodeToMessageConverter),
        };
    }
}