using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Avalonia.Headless.NUnit;
using Everywhere.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelsDevPresetModelProvider = Everywhere.AI.ModelsDevPresetModelProvider;

namespace Everywhere.Core.Tests.AI;

public class ModelsDevPresetModelProviderTests
{
    private const string Catalog = """
    {"deepseek":{"models":{
      "new-model":{"id":"new-model","name":"New model","tool_call":true,"modalities":{"input":["text","image"],"output":["text"]},"limit":{"context":128000,"output":8192},"knowledge":"2025-05","cost":{"input":1,"output":2}},
      "image-only":{"id":"image-only","name":"Image","modalities":{"input":["text"],"output":["image"]},"limit":{"context":128000,"output":8192}},
      "invalid":{"id":"invalid","modalities":{"input":["text"],"output":["text"]},"limit":{"context":0,"output":8192}}
    }}}
    """;

    [AvaloniaTest]
    public async Task Refresh_WhenValid_FiltersNonChatAndKeepsOtherProviders()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        using var handler = new DelegateHandler((_, _) => Task.FromResult(Response(Catalog)));
        using var provider = Create(handler, path);
        try
        {
            var originalOpenAI = provider.GetModelDefinitions("openai");
            await provider.RefreshAsync();
            Assert.That(provider.IsValidated, Is.True);
            Assert.That(provider.GetModelDefinitions("deepseek").Select(m => m.ModelId), Is.EqualTo(new[] { "new-model" }));
            Assert.That(provider.GetModelDefinitions("openai"), Is.SameAs(originalOpenAI));
            Assert.That(provider.GetValidatedModel("openai", "gpt-5.4"), Is.Null);
            Assert.That(provider.GetValidatedModel("deepseek", "new-model")?.KnowledgeCutoff, Is.EqualTo(new DateOnly(2025, 5, 1)));
            Assert.That(File.Exists(path), Is.True);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaTest]
    public async Task Startup_WithCache_RevalidatesWithETagBeforeMarkingModelsCurrent()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            using (var firstHandler = new DelegateHandler((_, _) => Task.FromResult(Response(Catalog))))
            using (var first = Create(firstHandler, path)) await first.RefreshAsync();
            var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? etag = null;
            using var handler = new DelegateHandler(async (request, token) =>
            {
                etag = request.Headers.IfNoneMatch.SingleOrDefault()?.ToString();
                requestStarted.SetResult();
                await releaseResponse.Task.WaitAsync(token);
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });
            using var provider = Create(handler, path);
            var validated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.ModelsChanged += (_, _) => { if (provider.IsValidated) validated.TrySetResult(); };
            await provider.InitializeAsync();
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(provider.GetModelDefinitions("deepseek").Single().ModelId, Is.EqualTo("new-model"));
            Assert.That(provider.GetValidatedModel("deepseek", "new-model"), Is.Null);
            Assert.That(etag, Is.EqualTo("\"catalog-v1\""));
            releaseResponse.SetResult();
            await validated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(provider.GetValidatedModel("deepseek", "new-model"), Is.Not.Null);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaTest]
    public async Task ConcurrentRefresh_ThenMalformedResponse_PreservesPublishedData()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DelegateHandler(async (_, token) =>
        {
            if (++calls == 1)
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
                return Response(Catalog);
            }
            return Response("not json");
        });
        using var provider = Create(handler, path);
        try
        {
            var refresh = provider.RefreshAsync();
            await started.Task;
            var joined = provider.RefreshAsync();
            Assert.That(calls, Is.EqualTo(1));
            release.SetResult();
            await Task.WhenAll(refresh, joined);
            var models = provider.GetModelDefinitions("deepseek");
            typeof(ModelsDevPresetModelProvider).GetField("_nextFetchAt", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(provider, DateTimeOffset.MinValue);
            await provider.RefreshAsync();
            Assert.That(provider.GetModelDefinitions("deepseek"), Is.SameAs(models));
            Assert.That(provider.IsBusy, Is.False);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaTest]
    public async Task Startup_WhenTransientFailure_RetriesWithoutBlockingInitialization()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var calls = 0;
        using var handler = new DelegateHandler((_, _) => Task.FromResult(++calls == 1 ?
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Response(Catalog)));
        using var provider = Create(handler, path);
        var validated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.ModelsChanged += (_, _) => { if (provider.IsValidated) validated.TrySetResult(); };
        try
        {
            var initialization = provider.InitializeAsync();
            Assert.That(initialization.IsCompletedSuccessfully, Is.True);
            await validated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(calls, Is.EqualTo(2));
        }
        finally { File.Delete(path); }
    }

    [AvaloniaTest]
    public async Task CancelWaitingCaller_DoesNotCancelSharedRefresh()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DelegateHandler(async (_, token) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            return Response(Catalog);
        });
        using var provider = Create(handler, path);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var original = provider.RefreshAsync();
            await started.Task;
            var waiter = provider.RefreshAsync(cancellationToken: cancellation.Token);
            cancellation.Cancel();
            await waiter;
            Assert.That(original.IsCompleted, Is.False);
            release.SetResult();
            await original;
            Assert.That(provider.IsValidated, Is.True);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaTest]
    public async Task Refresh_WithMixedModalitiesAndDates_KeepsTextOutputAndOrdersWithoutPromotingOldDefault()
    {
        var rows = new[]
        {
            ("deepseek", "deepseek-v4-flash", "Default", "2025-01-01", new[] { "text" }),
            ("deepseek", "month", "Month", "2026-08", new[] { "text" }),
            ("deepseek", "new", "Newest", "2026-08-02", new[] { "text" }),
            ("deepseek", "unknown", "Unknown", "invalid", new[] { "text" }),
            ("deepseek", "tie-b", "Month", "2026-08-01", new[] { "text" }),
            ("deepseek", "tie-a", "Month", "2026-08-01", new[] { "text" }),
            ("openai", "gpt-realtime-test", "Realtime", "2026-08-01", new[] { "text" }),
            ("openai", "audio", "Audio", "2026-08-01", new[] { "text", "audio" }),
            ("openai", "image", "Image", "2026-08-01", new[] { "text", "image" }),
            ("google", "deep-research-test", "Agent", "2026-08-01", new[] { "text" }),
            ("google", "gemini-test", "Chat", "2026-08-01", new[] { "text" }),
            ("mistral", "voxtral-small-latest", "Chat", "2026-08-01", new[] { "text" }),
            ("alibaba-cn", "qwen-mt-plus", "Translation", "2026-08-01", new[] { "text" }),
            ("alibaba", "qwen-deep-research", "Agent", "2026-08-01", new[] { "text" }),
            ("openrouter", "perplexity/sonar-deep-research", "Chat", "2026-08-01", new[] { "text" }),
            ("mistral", "audio-input", "Transcription", "2026-08-01", new[] { "text" }),
            ("openai", "audio-only", "Excluded", "2026-08-01", new[] { "audio" }),
            ("openai", "image-only", "Excluded", "2026-08-01", new[] { "image" }),
            ("openai", "video-only", "Excluded", "2026-08-01", new[] { "video" }),
            ("openai", "empty-output", "Excluded", "2026-08-01", Array.Empty<string>())
        };
        var catalog = System.Text.Json.JsonSerializer.Serialize(rows.GroupBy(r => r.Item1).ToDictionary(g => g.Key, g => new
        {
            models = g.ToDictionary(r => r.Item2, r => new
            {
                id = r.Item2, name = r.Item3, release_date = r.Item4,
                modalities = new { input = r.Item2 == "audio-input" ? new[] { "audio" } : new[] { "text", "audio" }, output = r.Item5 },
                limit = new { context = 32000, output = 4096 }
            })
        }));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        using var handler = new DelegateHandler((_, _) => Task.FromResult(Response(catalog)));
        using var provider = Create(handler, path);
        try
        {
            await provider.RefreshAsync();
            Assert.That(provider.GetModelDefinitions("deepseek").Select(m => m.ModelId),
                Is.EqualTo(new[] { "new", "month", "tie-a", "tie-b", "deepseek-v4-flash", "unknown" }));
            Assert.That(provider.GetValidatedModel("deepseek", "month")?.ReleaseDate, Is.EqualTo(new DateOnly(2026, 8, 1)));
            Assert.That(provider.GetValidatedModel("deepseek", "deepseek-v4-flash")?.IsDefault, Is.True);
            foreach (var row in rows.Skip(6))
            {
                var included = row.Item3 != "Excluded";
                Assert.That(provider.GetValidatedModel(row.Item1, row.Item2) is not null, Is.EqualTo(included), row.Item2);
            }
        }
        finally { File.Delete(path); }
    }

    private static ModelsDevPresetModelProvider Create(HttpMessageHandler handler, string path)
    {
        var provider = new ModelsDevPresetModelProvider(new ClientFactory(handler), NullLogger<ModelsDevPresetModelProvider>.Instance);
        typeof(ModelsDevPresetModelProvider).GetField("_cachePath", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(provider, path);
        return provider;
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json), Headers = { ETag = new EntityTagHeaderValue("\"catalog-v1\"") }
    };

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
