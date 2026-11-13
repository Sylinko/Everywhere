using System.Data.Common;
using System.Runtime.CompilerServices;
using Avalonia.Headless.NUnit;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Database;
using Everywhere.Extensions;
using Everywhere.Storage;
using Everywhere.Statistics;
using Everywhere.Statistics.Database;
using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.Chat;

[NonParallelizable]
public sealed class ChatShutdownTests
{
    // Exercise the actual startup ordering, raw on-disk format, migration and runtime restore.
    // The optional legacy import must see the loaded store, not an empty pre-initialization view.
    [AvaloniaTest]
    public async Task Initialize_WhenLegacyInputExists_LoadsStorageBeforeMigrationAndRestoresTheDraft()
    {
        using var folder = new IsolatedDataFolder();
        var legacy = new Dictionary<string, byte[]>
        {
            ["ChatInputAreaText"] = MessagePackSerializer.Serialize("legacy unsent question")
        };
        await File.WriteAllBytesAsync(Path.Combine(RuntimeConstants.WritableFolderPath, "storage.bin"), MessagePackSerializer.Serialize(legacy));
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<Settings>()
            .AddSingleton<PersistentKeyValueStorage>()
            .AddSingleton<IKeyValueStorage>(provider => provider.GetRequiredService<PersistentKeyValueStorage>())
            .AddSingleton<PersistentState>()
            .AddDatabaseAndStorage()
            .AddChatContextManager()
            .BuildServiceProvider();
        var serviceField = typeof(ServiceLocator).GetField("_serviceProvider", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new AssertionException("Missing application services.");
        var previous = serviceField.GetValue(null);
        serviceField.SetValue(null, services);
        try
        {
            var factory = services.GetRequiredService<IDbContextFactory<ChatV2DbContext>>();
            var manager = services.GetRequiredService<ChatContextManager>();
            var store = services.GetRequiredService<PersistentKeyValueStorage>();
            var initializers = new IAsyncInitializer[]
            {
                manager,
                new ChatV2StoreInitializer(factory, null, NullLogger<ChatV2StoreInitializer>.Instance),
                store
            };
            foreach (var initializer in initializers.OrderBy(initializer => initializer.Index))
            {
                await initializer.InitializeAsync(CancellationToken.None);
            }
            Assert.That(manager.Current.Input.Draft.Text, Is.EqualTo("legacy unsent question"));
            Assert.That(manager.Current.Data.Metadata.IsPersisted, Is.False);
            Assert.That(store.Get<string>("ChatInputAreaText"), Is.EqualTo("legacy unsent question"));
            store.Set("ChatInputAreaText", "obsolete value");
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
            Assert.That(await db.Chats.CountAsync(), Is.Zero);
            var input = await services.GetRequiredService<IChatContextStorage>().ReadNewChatInputAsync(CancellationToken.None);
            Assert.That(input?.ChatId, Is.EqualTo(manager.Current.Data.Metadata.Id));
            Assert.That(input?.Snapshot.Draft.Text, Is.EqualTo("legacy unsent question"));
        }
        finally
        {
            serviceField.SetValue(null, previous);
        }
    }

    [AvaloniaTest]
    public async Task DisposeAsync_ThroughServiceProvider_PersistsFinalChatStatisticsAndPendingRetry()
    {
        using var folder = new IsolatedDataFolder();
        var writes = new FailingNodeWriteInterceptor();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<Settings>()
            .AddDatabaseAndStorage()
            .AddChatContextManager()
            .ConfigureDbContext<ChatV2DbContext>(options => options.AddInterceptors(writes))
            .BuildServiceProvider();
        var chatFactory = services.GetRequiredService<IDbContextFactory<ChatV2DbContext>>();
        var statisticsFactory = services.GetRequiredService<IDbContextFactory<StatisticsDbContext>>();
        await using (var db = chatFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }
        await using (var db = statisticsFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var manager = services.GetRequiredService<ChatContextManager>();
        // Production exposes the same singleton through its concrete type and interface.
        Assert.That(services.GetRequiredService<IChatContextManager>(), Is.SameAs(manager));
        var root = manager.CreateChat(isTemporary: false);
        var message = new UserChatMessage("saved before shutdown", []);
        var node = await root.DispatchAsync(() => root.Data.Add(message));
        var chatId = root.Data.Metadata.Id;
        await manager.FlushPendingSavesAsync();

        var retryRoot = manager.CreateChat(isTemporary: false);
        var retryChatId = retryRoot.Data.Metadata.Id;
        await retryRoot.DispatchAsync(() => retryRoot.Data.Add(new UserChatMessage("pending retry", [])));
        writes.ShouldRejectWrites = true;
        try
        {
            await Assert.ThatAsync(() => manager.FlushPendingSavesAsync(), Throws.TypeOf<AggregateException>());
        }
        finally
        {
            writes.ShouldRejectWrites = false;
        }

        var recorder = services.GetRequiredService<IStatisticsRecorder>();
        var invocationId = Guid.CreateVersion7();
        await recorder.StartModelInvocationAsync(new StatisticsModelInvocationDraft(
            invocationId, null, chatId, node.Id, StatisticsModelInvocationPurpose.ChatResponse, "shutdown-model", DateTimeOffset.UtcNow));
        using var entered = new ManualResetEventSlim();
        var operation = root.TryStartAsync(() => node, async (_, token) =>
        {
            entered.Set();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                // DI must be the caller stopping this operation, not a pre-disposal flush.
                Assert.Throws<ObjectDisposedException>(() => services.GetRequiredService<Settings>());
                await root.DispatchAsync(() =>
                {
                    message.Content = "final cancellation output";
                    root.Data.Metadata.Rename("final title");
                });
                // Final accounting must persist after the operation token has been canceled.
                await recorder.CompleteModelInvocationAsync(
                    invocationId, new ChatUsageDetails { OutputTokenCount = 13 }, DateTimeOffset.UtcNow,
                    isSuccess: false, isCanceled: true, errorType: typeof(OperationCanceledException).FullName,
                    cancellationToken: CancellationToken.None);
            }
        }, IExceptionHandler.DangerouslyIgnoreAllException);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);

        // Match the platform entry points: dispose the provider, not the manager. Block the
        // Avalonia thread so persistence cannot accidentally depend on a live UI dispatcher.
        Task.Run(async () => await services.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Assert.That(operation.IsCompletedSuccessfully, Is.True, "The publication receipt finishes before cancellation cleanup.");
        Assert.That(operation.Result, Is.True);

        // The old factories and logger factory have now ended their lifetimes. Reopen both
        // databases independently, as a subsequent process would, to verify committed data.
        await using var chats = new ChatV2DbContext(new DbContextOptionsBuilder<ChatV2DbContext>()
            .UseSqlite($"Data Source={RuntimeConstants.GetDatabasePath("chat-v2.db")};Pooling=False").Options);
        var saved = await chats.Nodes.SingleAsync(row => row.ChatContextId == chatId && row.Id == node.Id);
        Assert.That(((UserChatMessage)MessagePackSerializer.Deserialize<ChatMessage>(saved.Payload)).Content,
            Is.EqualTo("final cancellation output"));
        Assert.That((await chats.Chats.SingleAsync(chat => chat.Id == chatId)).Topic, Is.EqualTo("final title"));
        Assert.That(await chats.Nodes.CountAsync(), Is.EqualTo(4));
        // This graph received no further notifications after its failed save.
        var retried = await chats.Nodes.SingleAsync(row => row.ChatContextId == retryChatId && row.MessageKind == ChatMessageKind.User);
        Assert.That(((UserChatMessage)MessagePackSerializer.Deserialize<ChatMessage>(retried.Payload)).Content, Is.EqualTo("pending retry"));

        await using var statistics = new StatisticsDbContext(new DbContextOptionsBuilder<StatisticsDbContext>()
            .UseSqlite($"Data Source={RuntimeConstants.GetDatabasePath("statistics.db")};Pooling=False").Options);
        var invocation = await statistics.ModelInvocationEvents.SingleAsync();
        Assert.That(invocation.Id, Is.EqualTo(invocationId));
        Assert.That(invocation.IsCanceled, Is.True);
        Assert.That(invocation.OutputTokenCount, Is.EqualTo(13));
    }

    // Use the production registration without letting it open the user's real databases.
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<WritableFolderPath>k__BackingField")]
    private static extern ref string WritableFolderPath(
        [UnsafeAccessorType("Everywhere.Common.RuntimeConstants, Everywhere.Abstractions")] object? unused);

    private sealed class FailingNodeWriteInterceptor : DbCommandInterceptor
    {
        public bool ShouldRejectWrites { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (ShouldRejectWrites && command.CommandText.StartsWith("INSERT INTO Nodes", StringComparison.Ordinal))
            {
                throw new IOException("The test disk is unavailable.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class IsolatedDataFolder : IDisposable
    {
        private readonly string _original = RuntimeConstants.WritableFolderPath;
        private readonly string _path = Path.Combine(Path.GetTempPath(), "Everywhere-shutdown-tests", Guid.NewGuid().ToString("N"));

        public IsolatedDataFolder()
        {
            Directory.CreateDirectory(_path);
            WritableFolderPath(null) = _path;
        }

        public void Dispose()
        {
            WritableFolderPath(null) = _original;
            SqliteConnection.ClearAllPools();
            Directory.Delete(_path, recursive: true);
        }
    }
}