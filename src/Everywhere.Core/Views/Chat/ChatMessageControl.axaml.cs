using Avalonia.Controls;

namespace Everywhere.Views;

/// <summary>
/// Renders an <see cref="Everywhere.Chat.ChatMessage"/> supplied through <see cref="ContentControl.Content"/>.
/// Node-level actions belong to <see cref="ChatMessageNodeControl"/> and are accessed through
/// its explicit <c>Node</c> property by the message templates.
/// </summary>
public class ChatMessageControl : ContentControl;