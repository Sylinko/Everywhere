using Avalonia.Data.Converters;
using Everywhere.Chat.Plugins;
using ZLinq;

namespace Everywhere.ValueConverters;

public static class ChatPluginTodoItemsValueConverters
{
    public static IValueConverter ToCompletedCount { get; } =
        new FuncValueConverter<IReadOnlyList<ChatPluginTodoItem>, int>(x =>
            x?.AsValueEnumerable().Count(i => i.Status == ChatPluginTodoStatus.Completed) ?? 0);
}