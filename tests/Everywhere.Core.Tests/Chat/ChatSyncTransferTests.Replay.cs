using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Everywhere.Chat;
using Everywhere.Cloud;
using Everywhere.Database;
using Everywhere.Harness;
using Everywhere.Harness.Providers.Gemini;
using Everywhere.Storage;
using Lucide.Avalonia;
using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Everywhere.Core.Tests.Chat;

partial class ChatSyncTransferTests
{
    [Test]
    public async Task Sync_WhenTurnCarriesReasoningMetadataAndToolFiles_PreservesContinuationAndBlobInventory()
    {
        const string hash = "synced-tool-blob";
        using var stepDoc = JsonDocument.Parse("""{"type":"google_search_call","id":"hosted-1","signature":"search-sig"}""");
        using var data = new ChatData();
        var turn = data.Add(new UserChatMessage("What's the weather?", []));
        var assistant = new AssistantChatMessage();
        assistant.AddSpan(new AssistantChatMessageReasoningSpan("Checking forecast")
        {
            Metadata = MetadataDictionary.FromDictionary(new Dictionary<string, object?>
            {
                [ReasoningMetadata.DialectKey] = GeminiInteractionsClient.DialectName,
                [ReasoningMetadata.ProtectedDataKey] = "thought-sig"
            })
        });
        assistant.AddSpan(new AssistantChatMessageReasoningSpan
        {
            Metadata = MetadataDictionary.FromDictionary(new Dictionary<string, object?>
            {
                [ReasoningMetadata.DialectKey] = GeminiInteractionsClient.DialectName,
                [ReasoningMetadata.GeminiStepKey] = stepDoc.RootElement.Clone()
            })
        });

        var callMessage = new FunctionCallChatMessage(LucideIconKind.Wrench, null);
        callMessage.AddCall(new ToolCallRecord("weather", null, "call-1", new Dictionary<string, object?> { ["city"] = "Tokyo" })
        {
            Metadata = new Dictionary<string, object?> { [ReasoningMetadata.DialectKey] = GeminiInteractionsClient.DialectName }
        });
        callMessage.AddResult(new ToolResultRecord(
            "weather",
            null,
            "call-1",
            new ToolContentResult
            {
                Blocks = [new ToolContentBlock.Text("radar map"), new ToolContentBlock.File(0)]
            })
        {
            Files = [new ToolResultFile(hash, "image/png")]
        });
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(callMessage)
        {
            Metadata = MetadataDictionary.FromDictionary(new Dictionary<string, object?>
            {
                [ReasoningMetadata.DialectKey] = GeminiInteractionsClient.DialectName
            })
        });
        assistant.AddSpan(new AssistantChatMessageTextSpan("Clear skies."));
        var node = data.Append(turn, assistant);

        var source = await SaveSourceAsync(data);
        await using (var db = Open())
        {
            Assert.That((await db.NodeBlobs.SingleAsync(row => row.ChatNodeId == node.Id)).BlobSha256, Is.EqualTo(hash));
        }

        await TransferAsync(source, data.Metadata.Id, async (destination, _, factory) =>
        {
            await using var reopened = await destination.LoadContextAsync(data.Metadata.Id);
            var syncedNode = reopened.Data.FindNode(node.Id) ?? throw new AssertionException("Missing synced assistant node.");
            await reopened.EnsureTurnLoadedAsync(syncedNode.Turn);
            var restored = (AssistantChatMessage)syncedNode.Message;

            var reasoningSpans = restored.Spans.OfType<AssistantChatMessageReasoningSpan>().ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(reasoningSpans, Has.Length.EqualTo(2));
                Assert.That(reasoningSpans[0].ReasoningOutput, Is.EqualTo("Checking forecast"));
                Assert.That(reasoningSpans[0].Metadata[ReasoningMetadata.ProtectedDataKey], Is.EqualTo("thought-sig"));
                Assert.That(reasoningSpans[1].Metadata[ReasoningMetadata.GeminiStepKey], Is.TypeOf<JsonElement>());
                var restoredStep = (JsonElement)reasoningSpans[1].Metadata[ReasoningMetadata.GeminiStepKey]!;
                Assert.That(restoredStep.GetProperty("signature").GetString(), Is.EqualTo("search-sig"));
            });

            var restoredCallSpan = restored.Spans.OfType<AssistantChatMessageFunctionCallSpan>().Single();
            var restoredResult = restoredCallSpan.Items.Single().Results.Single();
            Assert.That(restoredResult.Files.Single().Sha256, Is.EqualTo(hash));
            Assert.That(restoredResult.Files.Single().Attachment.FilePath, Is.Empty);

            restored.AddSpan(new AssistantChatMessageTextSpan("Continued on device B."));
            await destination.SaveDataAsync(reopened.Data);
            await using var destDb = await factory.CreateDbContextAsync();
            Assert.That((await destDb.NodeBlobs.SingleAsync(row => row.ChatNodeId == node.Id)).BlobSha256, Is.EqualTo(hash));
        });
    }

    private async Task<ChatTurnStorage> SaveSourceAsync(ChatData data)
    {
        var factory = Substitute.For<IDbContextFactory<ChatV2DbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Open()));
        var source = new ChatTurnStorage(factory) { SyncAccountId = "A" };
        await using var sender = Open();
        await new ChatSyncStateStore(sender).EnrollAsync("A", 1);
        await source.SaveDataAsync(data);
        return source;
    }

    private async Task TransferAsync(
        ChatTurnStorage source,
        Guid chatId,
        Func<ChatTurnStorage, HttpClient, IDbContextFactory<ChatV2DbContext>, Task> verify)
    {
        await using var sender = Open();
        await source.FreezeUploadAsync("A", chatId, true);
        var parts = await sender.SyncParts.AsNoTracking().OrderBy(p => p.Position).Select(p => p.Body).ToArrayAsync();

        using var frames = new MemoryStream();
        var head = new ChatSyncHead(chatId.ToString("D"), 1, ChatSyncLifecycle.Active, 1, ChatSyncAvailability.Available, parts.Sum(part => part.Length));
        AppendFrame(new ChatSyncSnapshot(Guid.NewGuid().ToString("D"), head));
        foreach (var payload in parts)
        {
            var part = MessagePackSerializer.Deserialize<ChatSyncUploadPart>(payload);
            var entry = new ChatSyncSnapshotEntry
            {
                Type = part.Type, Revision = 1, Id = part.Node?.Id ?? part.Id, ParentId = part.Node?.ParentId,
                TurnId = part.Node?.TurnId, Kind = part.Node?.Kind ?? 0, CreatedAt = part.Node?.CreatedAt,
                SummaryThroughNodeId = part.Node?.SummaryThroughNodeId, ChildId = part.ChildId, Name = part.Name
            };
            AppendFrame(new ChatSyncSnapshotPart(entry, part.Body ?? [], part.Node is null || part.IsComplete));
        }

        AppendFrame<ChatSyncSnapshotPart?>(null);
        using var http = CreateClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Delete) return Task.FromResult(Reply<object?>(null));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(frames.ToArray()) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.everywhere.snapshot-stream");
            return Task.FromResult(response);
        }));
        var transport = new ChatSyncTransport(http, "A", new ChatSyncCompatibility(2, 1, 1));
        using var snapshot = await ChatSyncDownloadedSnapshot.ReadAsync(transport, chatId, 0, CancellationToken.None);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ChatV2DbContext>().UseSqlite(connection).Options;
        var otherFactory = Substitute.For<IDbContextFactory<ChatV2DbContext>>();
        otherFactory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new ChatV2DbContext(options)));
        await using (var db = new ChatV2DbContext(options))
        {
            await db.Database.MigrateAsync();
            await new ChatSyncStateStore(db).EnrollAsync("A", 1);
        }

        var destination = new ChatTurnStorage(otherFactory) { SyncAccountId = "A" };
        await destination.MergeSnapshotAsync("A", snapshot, new HashSet<Guid>(), false, CancellationToken.None);
        await verify(destination, http, otherFactory);

        void AppendFrame<T>(T value)
        {
            var payload = MessagePackSerializer.Serialize(value);
            var length = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
            frames.Write(length);
            frames.Write(payload);
        }
    }
}
