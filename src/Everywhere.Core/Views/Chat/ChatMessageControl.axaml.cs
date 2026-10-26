using Avalonia.Controls;
using Avalonia.Data;
using Everywhere.Chat;

namespace Everywhere.Views;

/// <summary>
/// Renders the <see cref="ChatMessageState"/> of a fixed row through <see cref="ContentControl.Content"/>.
/// Templates use <see cref="Row"/> for node-level actions without replacing the inherited data context.
/// </summary>
public sealed class ChatMessageControl : ContentControl
{
    /// <summary>
    /// Gets the stable row rendered for this control's lifetime.
    /// </summary>
    public ChatMessagePresentationRow Row { get; }

    /// <summary>
    /// Binds changing snapshots from a fixed row. The outer row presenter owns container recycling.
    /// </summary>
    public ChatMessageControl(ChatMessagePresentationRow row)
    {
        Row = row;
        this[!ContentProperty] = new Binding(nameof(ChatMessagePresentationRow.State))
        {
            Source = row,
            Mode = BindingMode.OneWay
        };
    }
}