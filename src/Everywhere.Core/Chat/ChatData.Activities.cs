using Lucide.Avalonia;

namespace Everywhere.Chat;

partial class ChatData
{
    /// <summary>
    /// Starts a runtime activity without materializing a view. An explicit owner keeps work on its
    /// execution branch; otherwise the selected tail is used.
    /// </summary>
    public IBusyActivity StartActivity(
        LucideIconKind icon,
        IDynamicLocaleKey headerKey,
        bool removeAfterCompletion,
        IDynamicLocaleKey? secondaryHeaderKey = null,
        ChatMessage? owner = null)
    {
        var ownerNode = owner is null ? Items.AsValueEnumerable().LastOrDefault() : owner.Owner;
        if (ownerNode?.Turn is null || !ReferenceEquals(ownerNode.Data, this) || IsDisposed)
        {
            ownerNode = null;
        }

        var activity = new ChatBusyActivity(ownerNode, icon, headerKey, secondaryHeaderKey, removeAfterCompletion);
        ownerNode?.Turn?.AddActivity(activity);
        return activity;
    }
}