using Everywhere.Chat;
using Everywhere.Storage;

namespace Everywhere.Core.Tests.Chat;

internal static class ChatStorageTestExtensions
{
    // Some persistence fixtures need to register their exact graph rather than deserialize a copy.
    // Keep that arrangement in tests; production roots are created only by the manager.
    public static RootChatContext AttachRoot(ChatContextManager manager, ChatData data)
    {
        var create = typeof(ChatContextManager).GetMethod("CreateRoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new AssertionException("Expected the manager's root creation boundary.");
        return (RootChatContext)(create.Invoke(manager, [data]) ?? throw new AssertionException("Expected a root context."));
    }

    public static async Task SaveDataAsync(this IChatContextStorage storage, ChatData data)
    {
        var (batch, changes) = ChatWriteBatch.Capture(data);
        await storage.SaveChatContextAsync(batch);
        data.PersistenceDirtyState.Acknowledge(changes);
    }

    public static async Task<RootChatContext> LoadContextAsync(this IChatContextStorage storage, Guid id)
    {
        ChatHistoryCursor? cursor = null;
        do
        {
            var page = await storage.ReadHistoryPageAsync(20, cursor);
            foreach (var metadata in page.Items)
            {
                if (metadata.Id != id) continue;
                var data = await storage.GetChatContextAsync(metadata);
                return new RootChatContext(data, storage);
            }
            cursor = page.NextCursor;
        } while (cursor is not null);

        throw new AssertionException($"Missing test conversation {id}.");
    }
}