using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Everywhere.Views;

/// <summary>
/// Creates a message control whose row remains fixed while its snapshots change.
/// </summary>
public sealed class ChatMessageControlTemplate : ITypedDataTemplate
{
    /// <inheritdoc />
    public Type DataType => typeof(ChatMessagePresentationRow);

    /// <inheritdoc />
    public Control? Build(object? param)
    {
        return param is ChatMessagePresentationRow row ? new ChatMessageControl(row) : null;
    }

    /// <inheritdoc />
    public bool Match(object? data) => data is ChatMessagePresentationRow;
}