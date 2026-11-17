using System.Net;
using System.Reactive.Disposables;
using System.Runtime.CompilerServices;
using Avalonia.Headless.NUnit;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Automation;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.BuiltIn;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Core.I18N;
using Everywhere.Core.Tests.Chat;
using Everywhere.Core.Tests.Statistics;
using Everywhere.Harness;
using Everywhere.I18N;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.Skills;
using Everywhere.Statistics;
using Everywhere.Storage;
using Lucide.Avalonia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkiaSharp;
using MEAI = Microsoft.Extensions.AI;

namespace Everywhere.Core.Tests.Harness;

public sealed class ImageViewRecoveryTests
{
    [AvaloniaTest]
    public Task Generate_WhenImagesExceedPayloadLimit_UnloadsOnlyOlderOccurrencesBeforeRetryAndAfterReload()
    {
        return VerifyImageRecoveryAsync(shouldRejectLatestBatch: false);
    }

    [AvaloniaTest]
    public Task Generate_WhenLatestImageBatchStillExceedsLimit_ContinuesWithExplicitReferences()
    {
        return VerifyImageRecoveryAsync(shouldRejectLatestBatch: true);
    }

    private static async Task VerifyImageRecoveryAsync(bool shouldRejectLatestBatch)
    {
        using var images = new TestImages();
        using var database = ChatTestDatabase.Create();
        var dbFactory = database.Factory;
        var storage = new ChatTurnStorage(dbFactory);
        var blobStorage = new BlobStorage(dbFactory);
        GetBlobBasePath(blobStorage) = images.BlobDirectory.FullName;
        var jpegPath = images.Jpeg.Path;
        var largePath = images.Large.Path;

        var serviceProvider = Substitute.For<IServiceProvider>();
        var settings = new Settings(serviceProvider);
        settings.Plugin.ApprovalMode = ToolApprovalMode.FullAccess;
        var specializations = new AssistantSpecializationResolver(
            new AssistantCatalog(Substitute.For<IPresetModelProvider>(), Substitute.For<IOfficialModelProvider>()));
        using var essential = new EssentialPlugin(settings, specializations, blobStorage);
        var pluginManager = new ChatPluginManager(
            serviceProvider, [essential], settings, Substitute.For<IRuntimeManager>(), NullLogger<ChatPluginManager>.Instance);
        using var releasePlugins = Disposable.Create(pluginManager.Dispose);
        await using var manager = new ChatContextManager(settings, storage, NullLogger<ChatContextManager>.Instance);
        var root = manager.Current;
        var data = root.Data;

        var attachment = await FileAttachment.CreateAsync(jpegPath, "image/jpeg");
        var first = data.Add(new UserChatMessage("Inspect the older image", [attachment]));
        await manager.FlushPendingChatAsync(root, CancellationToken.None);
        var olderImage = ChatImage.Read(jpegPath);
        using var olderStream = new MemoryStream(olderImage.Bytes, writable: false);
        var olderBlob = await blobStorage.StorageBlobAsync(olderStream, olderImage.MimeType);
        var olderPreview = new FileAttachment(attachment.HeaderKey, olderBlob.LocalPath, olderBlob.Sha256, olderImage.MimeType);
        var earlierTool = new FunctionCallChatMessage(LucideIconKind.Image, new DirectLocaleKey("View images"));
        earlierTool.AddCall(new ToolCallRecord("view_image", "essential", "old-view", null));
        earlierTool.AddResult(new ToolResultRecord("view_image", "essential", "old-view", new ViewImageFunctionResult(
        [
            ViewImageItem.FromPreparedImage(jpegPath, olderPreview, olderImage)
        ])));
        var olderAssistant = new AssistantChatMessage();
        olderAssistant.AddSpan(new AssistantChatMessageFunctionCallSpan(earlierTool));
        var shared = data.Append(first, olderAssistant);
        var alternative = data.Append(shared, new UserChatMessage("Other branch", []), shouldSelectChild: false);
        var user = data.Append(shared, new UserChatMessage("Look at the old image again and compare the large one", []));
        var assistant = data.Append(user, new AssistantChatMessage { IsBusy = true });

        var requests = new List<MEAI.ChatMessage[]>();
        var wasOffloadSavedBeforeRetry = false;
        var client = Substitute.For<MEAI.IChatClient>();
        client.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(), Arg.Any<MEAI.ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => RespondAsync(call.ArgAt<IEnumerable<MEAI.ChatMessage>>(0).ToArray()));
        var configuration = new AdvancedAssistantConfiguration
        {
            ModelId = "image-test",
            SupportsToolCall = true,
            InputModalities = Modalities.Text | Modalities.Image
        };
        var assistantSettings = new CustomAssistant { Configuration = configuration };
        var factory = Substitute.For<IModelSessionFactory>();
        factory.Create(Arg.Any<Assistant>()).Returns(_ => new TestSession(configuration, client));
        using var visual = new ChatVisualService(new UnusedHostConnectionSource());
        var chatService = new ChatService(
            manager, pluginManager, factory, specializations, blobStorage, settings, Substitute.For<IPromptService>(),
            Substitute.For<ISkillPromptProvider>(), Substitute.For<IStatisticsRecorder>(), visual,
            Substitute.For<IVisualContextScanEffect>(), serviceProvider, NullLogger<ChatService>.Instance);

        await root.ExecuteAsync(async token =>
        {
            var result = await chatService.GenerateAsync(
                root, assistantSettings, assistant, CompressionBoundary.Before(user),
                systemPromptOverride: "Compare the images.", enableNotifications: false, cancellationToken: token);
            Assert.That(result.Error, Is.Null);
            var expectedReply = shouldRejectLatestBatch ? "I need to view fewer images at a time." : "Comparison completed.";
            Assert.That(result.Output, Is.EqualTo(expectedReply));
        });

        var original = await File.ReadAllBytesAsync(jpegPath);
        var imageCounts = requests.Select(history => ImagesIn(history).Length).ToArray();
        var expectedCounts = shouldRejectLatestBatch ? new[] { 1, 3, 2, 0 } : [1, 3, 2];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wasOffloadSavedBeforeRetry, Is.True);
            Assert.That(imageCounts, Is.EqualTo(expectedCounts));
            Assert.That(ImagesIn(requests[2])[0].Data.ToArray(), Is.EqualTo(original));
            Assert.That(ImagesIn(requests[2])[0].MediaType, Is.EqualTo("image/jpeg"));
            Assert.That(requests[2].SelectMany(message => message.Contents).OfType<MEAI.TextContent>().Select(item => item.Text),
                Has.Some.Contains("Image omitted to fit the request").And.Contains("old-view"));
            if (shouldRejectLatestBatch)
            {
                Assert.That(requests[^1].SelectMany(message => message.Contents).OfType<MEAI.TextContent>().Select(item => item.Text),
                    Has.Some.Contains("Image omitted to fit the request").And.Contains("new-view"));
            }
        }

        await manager.FlushPendingChatAsync(root, CancellationToken.None);
        await File.WriteAllTextAsync(jpegPath, "The source file changed after the image was viewed.");
        await using var reopened = await storage.LoadContextAsync(data.Metadata.Id);
        foreach (var turn in reopened.Data.SelectedTurns)
        {
            await reopened.EnsureTurnLoadedAsync(turn);
        }

        var loaded = (AssistantChatMessage)(reopened.Data.FindNode(assistant.Id)?.Message ?? throw new AssertionException("Missing response."));
        var loadedCall = loaded.Spans.OfType<AssistantChatMessageFunctionCallSpan>().Single().Items.Single();
        var viewed = loadedCall.Results.Single().Result as ViewImageFunctionResult ?? throw new AssertionException("Missing image result.");
        var entries = viewed.Items;
        var resized = (ViewImageItem.Success)entries[2];
        var registeredFile = await blobStorage.QueryBlobAsync(attachment.Sha256);
        var state = loadedCall.Capture();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registeredFile?.LocalPath, Is.EqualTo(jpegPath));
            Assert.That(((ViewImageItem.Success)entries[0]).Preview.FilePath, Is.Not.EqualTo(jpegPath));
            Assert.That(entries.Select(item => item.GetType()),
                Is.EqualTo(new[] { typeof(ViewImageItem.Success), typeof(ViewImageItem.Failure), typeof(ViewImageItem.Success) }));
            Assert.That(entries.Select(item => item.SourcePath), Is.EqualTo(new[] { jpegPath, string.Empty, largePath }));
            Assert.That(((ViewImageItem.Failure)entries[1]).ErrorMessageKey,
                Is.EqualTo(new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_ViewImage_EmptyPath)));
            Assert.That(resized.OriginalWidth, Is.EqualTo(2400));
            Assert.That(resized.Width, Is.EqualTo(1920));
            Assert.That(resized.Height, Is.EqualTo(320));
            Assert.That(state.ImageViews.Single(), Is.SameAs(viewed));
            Assert.That(state.DisplayBlocks, Is.Empty);
            Assert.That(state.HasDetails, Is.True);
        }

        var renderer = Substitute.For<IPromptRenderer>();
        var reopenedHistory = await ChatHistoryBuilder.BuildChatHistoryAsync(
            renderer, "system", reopened.Data.SelectedNodes, -1, configuration.InputModalities, 0);
        var alternativeNode = reopened.Data.FindNode(alternative.Id) ?? throw new AssertionException("Missing alternate branch.");
        await reopened.EnsureTurnLoadedAsync(alternativeNode.Turn);
        var alternativeHistory = await ChatHistoryBuilder.BuildChatHistoryAsync(
            renderer, "system", reopened.Data.GetPathTo(alternativeNode), -1, configuration.InputModalities, 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ImagesIn(reopenedHistory), Has.Length.EqualTo(expectedCounts[^1]));
            Assert.That(ImagesIn(alternativeHistory), Has.Length.EqualTo(1));
            Assert.That(ImagesIn(alternativeHistory)[0].Data.ToArray(), Is.EqualTo(original));
        }

        async IAsyncEnumerable<MEAI.ChatResponseUpdate> RespondAsync(MEAI.ChatMessage[] history)
        {
            requests.Add(history);
            switch (requests.Count)
            {
                case 1:
                {
                    var arguments = new Dictionary<string, object?> { ["paths"] = new[] { jpegPath, string.Empty, largePath } };
                    yield return new MEAI.ChatResponseUpdate
                    {
                        Role = MEAI.ChatRole.Assistant,
                        Contents = [new MEAI.FunctionCallContent("new-view", "view_image", arguments)]
                    };
                    yield break;
                }
                case 2:
                {
                    throw new HttpRequestException("Request too large", null, HttpStatusCode.RequestEntityTooLarge);
                }
                case 3:
                {
                    await using var db = await dbFactory.CreateDbContextAsync();
                    wasOffloadSavedBeforeRetry = await db.Nodes.AnyAsync(node =>
                        node.ChatContextId == data.Metadata.Id && node.MessageKind == ChatMessageKind.ImageOffload);
                    if (shouldRejectLatestBatch)
                    {
                        throw new HttpRequestException("Request too large", null, HttpStatusCode.RequestEntityTooLarge);
                    }

                    break;
                }
                case 4 when shouldRejectLatestBatch:
                {
                    break;
                }
                default:
                {
                    throw new AssertionException("The image request was retried more than necessary.");
                }
            }

            var reply = shouldRejectLatestBatch ? "I need to view fewer images at a time." : "Comparison completed.";
            yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, reply);
        }
    }

    [AvaloniaTest]
    public async Task CreateScope_WhenModelCannotSeeImages_OmitsViewImage()
    {
        var provider = Substitute.For<IServiceProvider>();
        var settings = new Settings(provider);
        using var essential = new EssentialPlugin(
            settings,
            new AssistantSpecializationResolver(new AssistantCatalog(Substitute.For<IPresetModelProvider>(), Substitute.For<IOfficialModelProvider>())),
            Substitute.For<IBlobStorage>());
        var manager = new ChatPluginManager(
            provider, [essential], settings, Substitute.For<IRuntimeManager>(), NullLogger<ChatPluginManager>.Instance);
        using var releasePlugins = Disposable.Create(manager.Dispose);
        await using var root = new RootChatContext(new ChatData(), null);
        var scope = await manager.CreateScopeAsync(
            new AdvancedAssistantConfiguration { InputModalities = Modalities.Text }, root, root.Data.RootNode, null, CancellationToken.None);
        var functions = scope.Plugins.Single().GetChatFunctions().Select(function => function.Function.Name).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(functions, Does.Contain("manage_todo"));
            Assert.That(functions, Does.Not.Contain("view_image"));
        }
    }

    [Test]
    public void Read_WhenPngFitsLimits_PreservesBytes()
    {
        using var file = new TempImageFile();
        WriteImage(file.Path, SKEncodedImageFormat.Png, 24, 16);
        var prepared = ChatImage.Read(file.Path);
        Assert.That(prepared.Bytes, Is.EqualTo(File.ReadAllBytes(file.Path)));
    }

    private static MEAI.DataContent[] ImagesIn(IEnumerable<MEAI.ChatMessage> history)
    {
        return history.SelectMany(message => message.Contents).OfType<MEAI.DataContent>().ToArray();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_blobBasePath")]
    private static extern ref string GetBlobBasePath(BlobStorage storage);

    private static void WriteImage(string path, SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var encoded = bitmap.Encode(format, 92);
        using var file = File.Create(path);
        encoded.SaveTo(file);
    }

    private sealed class TestImages : IDisposable
    {
        public TempImageFile Jpeg { get; } = new();
        public TempImageFile Large { get; } = new();
        public DirectoryInfo BlobDirectory { get; } = Directory.CreateTempSubdirectory("Everywhere-ImageView-");

        public TestImages()
        {
            WriteImage(Jpeg.Path, SKEncodedImageFormat.Jpeg, 24, 16);
            WriteImage(Large.Path, SKEncodedImageFormat.Png, 2400, 400);
        }

        public void Dispose()
        {
            Jpeg.Dispose();
            Large.Dispose();
            BlobDirectory.Delete(recursive: true);
        }
    }

    private sealed class TempImageFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.GetTempFileName();

        public void Dispose()
        {
            File.Delete(Path);
        }
    }

    private sealed class TestSession(AssistantConfiguration configuration, MEAI.IChatClient client)
        : ModelSession(configuration, new ModelConnection(ModelProviderSchema.OpenAI, "https://test.invalid", null, new HttpClient(), null, 0))
    {
        public override MEAI.IChatClient ChatClient => client;
        public override string Dialect => "openai";
    }

    internal sealed class UnusedHostConnectionSource : IHostConnectionSource
    {
        public ValueTask<RpcConnection> GetConnectionAsync(ProcessRole role, CancellationToken cancellationToken)
        {
            throw new AssertionException("Local image views do not require the automation host.");
        }

        public IAsyncEnumerable<RpcConnection> WatchConnectionsAsync(ProcessRole role, CancellationToken cancellationToken)
        {
            throw new AssertionException("Local image views do not require the automation host.");
        }
    }
}
