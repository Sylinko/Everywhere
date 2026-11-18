using System.Net;
using System.Net.Http.Headers;
using Everywhere.Cloud;
using Everywhere.Chat;
using Everywhere.Database;
using Everywhere.Storage;
using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Everywhere.Core.Tests.Chat;

public sealed partial class ChatSyncTransferTests
{
    private string _path = string.Empty;

    [SetUp]
    public async Task SetUp()
    {
        _path = Path.Combine(Path.GetTempPath(), $"everywhere-sync-transfer-{Guid.NewGuid():N}.db");
        await using var db = Open();
        await db.Database.MigrateAsync();
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    // Protect the response-to-transaction cancellation boundary; recovery must rediscover the same page.
    [Test]
    public async Task Discovery_WhenResponseIsCanceled_RetriesFromDurableCursor()
    {
        await using (var db = Open())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await new ChatSyncStateStore(db).EnrollAsync("A", 1);
            await transaction.CommitAsync();
        }
        using var cancellation = new CancellationTokenSource();
        var page = new ChatSyncDiscovery([new(Guid.NewGuid().ToString("D"), 7, ChatSyncLifecycle.Active, 1, ChatSyncAvailability.Available, 1)], 7, false);
        var shouldCancel = true;
        using var http = CreateClient(new Handler((request, token) =>
        {
            Assert.That(request.RequestUri?.Query, Is.EqualTo("?after=0"));
            if (shouldCancel)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.FromResult(Reply(page));
        }));
        await Assert.CatchAsync<OperationCanceledException>(async () => await Transfer(http).DiscoverAsync(cancellation.Token));
        shouldCancel = false;
        Assert.That((await Transfer(http).DiscoverAsync()).HasMore, Is.False);
        await using var reopened = Open();
        Assert.Multiple(() =>
        {
            Assert.That(reopened.SyncAccounts.Single().ScanCursor, Is.EqualTo(7));
            Assert.That(reopened.SyncChats.Single().DiscoveredRevision, Is.EqualTo(7));
            Assert.That(reopened.SyncChats.Single().AppliedRevision, Is.Zero);
        });
    }

    // An unresolved republish may already have succeeded remotely. A later permanent delete
    // must survive exact-request recovery and must not materialize that rebuilt chat again.
    [Test]
    public async Task Delete_WhenRepublishIsUncertain_ResolvesReceiptThenDeletesItsSuccessor()
    {
        var factory = Substitute.For<IDbContextFactory<ChatV2DbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Open()));
        var storage = new ChatTurnStorage(factory) { SyncAccountId = "A" };
        await using (var db = Open())
        {
            await new ChatSyncStateStore(db).EnrollAsync("A", 1);
        }
        using var data = new ChatData();
        data.StartTurn(new UserChatMessage("retained copy", []));
        await storage.SaveDataAsync(data);
        var id = data.Metadata.Id;
        var cleared = new ChatSyncHead(id.ToString("D"), 5, ChatSyncLifecycle.Active, 1, ChatSyncAvailability.Cleared, 0);
        await storage.ApplyClearedHeadAsync("A", cleared, CancellationToken.None);
        await storage.RequestRepublishAsync("A", id, 5, CancellationToken.None);
        await storage.FreezeUploadAsync("A", id, true, CancellationToken.None);
        Guid commitId;
        await using (var db = Open())
        {
            commitId = (await db.SyncSubmissions.AsNoTracking().SingleAsync()).CommitId;
        }
        await storage.DeletePermanentlyAsync(id);
        using var http = CreateClient(new Handler((_, _) =>
            Task.FromResult(Reply(new ChatSyncCommitResult(commitId.ToString("D"), 6, 100, 1024)))));
        await Transfer(http).UploadAsync(id);
        await using (var db = Open())
        {
            var deletion = await db.SyncPending.AsNoTracking().SingleAsync(row => row.Kind == ChatSyncRecordKind.Lifecycle);
            Assert.That(deletion.Key, Is.EqualTo("delete"));
            Assert.That(deletion.BaseRevision, Is.EqualTo(6));
            Assert.That(await db.Chats.CountAsync(), Is.Zero);
            Assert.That(await db.SyncSubmissions.CountAsync(), Is.Zero);
        }

        var rebuilt = new ChatSyncHead(id.ToString("D"), 6, ChatSyncLifecycle.Active, 6, ChatSyncAvailability.Available, 100, 6);
        // The pending-delete path needs only the staged head; it must never enumerate bodies.
        using var snapshot = Activator.CreateInstance(typeof(ChatSyncDownloadedSnapshot),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, [rebuilt, 5L], null)
            as ChatSyncDownloadedSnapshot ?? throw new AssertionException("Missing snapshot constructor.");
        var installed = await storage.MergeSnapshotAsync("A", snapshot, new HashSet<Guid>(), false, CancellationToken.None);
        Assert.That(installed?.Metadata, Is.Null);
        await storage.FreezeUploadAsync("A", id, true, CancellationToken.None);
        await using var reopened = Open();
        var header = MessagePackSerializer.Deserialize<ChatSyncUploadHeader>((await reopened.SyncSubmissions.SingleAsync()).Header);
        Assert.That(header.Action, Is.EqualTo(ChatSyncAction.Delete));
        Assert.That(header.ExpectedRevision, Is.EqualTo(6));
        Assert.That(header.ExpectedLifecycleRevision, Is.EqualTo(6));
        Assert.That(await reopened.Chats.CountAsync(), Is.Zero);
    }

    // A new serializer reading its own output cannot detect a break in the pre-Harness SK wire layout.
    [Test]
    public async Task FreezeUpload_WhenContinuingPreHarnessSignedToolNode_PreservesToolIdentityAndMetadata()
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures/Harness/v2-signed-tool.msgpack"));
        using var data = new ChatData();
        data.StartTurn(new UserChatMessage("question", []));
        var assistant = (AssistantChatMessage)MessagePackSerializer.Deserialize<ChatMessage>(bytes);
        var node = data.Append(assistant);
        assistant.AddSpan(new AssistantChatMessageTextSpan("continued"));

        var factory = Substitute.For<IDbContextFactory<ChatV2DbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Open()));
        var storage = new ChatTurnStorage(factory) { SyncAccountId = "A" };
        await using (var db = Open())
        {
            await new ChatSyncStateStore(db).EnrollAsync("A", 1);
        }
        await storage.SaveDataAsync(data);
        await storage.FreezeUploadAsync("A", data.Metadata.Id, true);
        await using var reopened = Open();
        var submission = await reopened.SyncSubmissions.SingleAsync();
        var header = MessagePackSerializer.Deserialize<ChatSyncUploadHeader>(submission.Header);
        var parts = await reopened.SyncParts.OrderBy(row => row.Position).Select(row => row.Body).ToArrayAsync();
        var part = parts.Select(body => MessagePackSerializer.Deserialize<ChatSyncUploadPart>(body)).Single(p => p.Node?.Id == node.Id.ToString("D"));
        var payload = part.Body ?? throw new AssertionException("Missing frozen assistant payload.");
        using var restored = (AssistantChatMessage)MessagePackSerializer.Deserialize<ChatMessage>(payload);
        var group = restored.Items.OfType<AssistantChatMessageFunctionCallSpan>().Single().Items.Single();
        var call = group.Calls.Single();
        var result = group.Results.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.RequiredDataGeneration, Is.EqualTo(1));
            Assert.That(restored.ToString(), Does.Contain("public-before").And.Contain("continued"));
            Assert.That(call.Id, Is.EqualTo("call-123"));
            Assert.That(call.PluginName, Is.EqualTo("fixtures"));
            Assert.That(call.FunctionName, Is.EqualTo("lookup"));
            Assert.That(call.Arguments, Does.ContainKey("optional"));
            Assert.That(call.Metadata?["thoughtSignature"], Is.EqualTo("call-owned-signature"));
            Assert.That(result.CallId, Is.EqualTo(call.Id));
            Assert.That(result.Result, Is.EqualTo("result-123"));
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://sync.example/") };

    private ChatSyncTransfer Transfer(HttpClient http)
    {
        var factory = Substitute.For<IDbContextFactory<ChatV2DbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Open()));
        var transport = new ChatSyncTransport(http, "A", new ChatSyncCompatibility(2, 1, 1));
        return new ChatSyncTransfer(factory, transport);
    }

    private ChatV2DbContext Open()
    {
        var options = new DbContextOptionsBuilder<ChatV2DbContext>().UseSqlite($"Data Source={_path};Pooling=False;Foreign Keys=True").Options;
        return new ChatV2DbContext(options);
    }

    private static HttpResponseMessage Reply<T>(T value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MessagePackSerializer.Serialize(value)) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/msgpack");
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return send(request, cancellationToken);
        }
    }
}
