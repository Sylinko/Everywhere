using Avalonia.Controls;
using Avalonia.Input;

namespace Everywhere.Views;

/// <summary>
/// Opens history context menus without selecting or loading the targeted conversation.
/// </summary>
public sealed class ChatHistoryListBox : ListBox
{
    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(ListBox);

    /// <inheritdoc />
    protected override bool ShouldTriggerSelection(Visual selectable, PointerEventArgs eventArgs)
    {
        if (eventArgs.Properties.PointerUpdateKind is PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased)
        {
            return false;
        }

        return base.ShouldTriggerSelection(selectable, eventArgs);
    }
}