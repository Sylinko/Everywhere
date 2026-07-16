using Avalonia.Controls.Primitives;
using Everywhere.Chat;

namespace Everywhere.Views;

/// <summary>
/// Owns the lightweight dialog surface used to inspect a subagent conversation.
/// The dialog deliberately contains only a normal chat projection; it does not navigate or
/// mutate the outer chat viewport.
/// </summary>
public sealed class ChatSubagentView : TemplatedControl
{
    /// <summary>
    /// Defines the <see cref="ChatContext"/> property.
    /// </summary>
    public static readonly StyledProperty<ChatContext?> ChatContextProperty =
        AvaloniaProperty.Register<ChatSubagentView, ChatContext?>(nameof(ChatContext));

    /// <summary>
    /// Gets or sets the subagent conversation projected inside this dialog.
    /// </summary>
    public ChatContext? ChatContext
    {
        get => GetValue(ChatContextProperty);
        set => SetValue(ChatContextProperty, value);
    }
}