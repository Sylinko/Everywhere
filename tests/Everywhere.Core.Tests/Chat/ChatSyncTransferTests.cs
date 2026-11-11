using System.Net;
using System.Net.Http.Headers;
using Everywhere.Cloud;
using Everywhere.Database;
using Everywhere.Storage;
using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Everywhere.Core.Tests.Chat;

public sealed class ChatSyncTransferTests
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
        Assert.CatchAsync<OperationCanceledException>(async () => await Transfer(http).DiscoverAsync(cancellation.Token));
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