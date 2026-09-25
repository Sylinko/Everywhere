using System.Net;
using System.Net.Http.Headers;
using Everywhere.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.AI;

public class ModelCatalogHttpClientTests
{
    [Test]
    public async Task Invalidate_DuringFetch_DropsObsoleteResponseAndCacheRevision()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DelegateHandler(async (_, token) =>
        {
            requestStarted.SetResult();
            await releaseResponse.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("obsolete") };
        });
        var cache = new MemoryCacheStore();
        var published = new List<string>();
        using var coordinator = new ProbeProvider(
            new ProbeClient(new ClientFactory(handler)),
            cache,
            published);

        var refresh = coordinator.RefreshAsync();
        await requestStarted.Task;
        await coordinator.InvalidateAsync();
        releaseResponse.SetResult();
        await refresh;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(published, Is.Empty);
            Assert.That(cache.Entry, Is.Null);
            Assert.That(cache.SaveCount, Is.Zero);
        }
    }

    [Test]
    public async Task Invalidate_DuringCacheWrite_ClearsObsoleteRevisionAfterWriteCompletes()
    {
        using var handler = new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("obsolete") }));
        var cache = new BlockingCacheStore();
        var published = new List<string>();
        using var coordinator = new ProbeProvider(
            new ProbeClient(new ClientFactory(handler)),
            cache,
            published);

        var refresh = coordinator.RefreshAsync();
        await cache.SaveStarted.Task;
        var invalidation = coordinator.InvalidateAsync();
        cache.ReleaseSave.SetResult();
        await Task.WhenAll(refresh, invalidation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(published, Is.Empty);
            Assert.That(cache.Entry, Is.Null);
            Assert.That(cache.ClearCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Invalidate_WhenObsoleteRequestDoesNotStop_AllowsCurrentRevisionToRefresh()
    {
        var firstRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var handler = new DelegateHandler(async (_, _) =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                firstRequestStarted.SetResult();
                await releaseFirstResponse.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("obsolete") };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("current") };
        });
        var cache = new MemoryCacheStore();
        var published = new List<string>();
        using var coordinator = new ProbeProvider(
            new ProbeClient(new ClientFactory(handler)),
            cache,
            published);

        var obsoleteRefresh = coordinator.RefreshAsync();
        await firstRequestStarted.Task;
        await coordinator.InvalidateAsync();
        await coordinator.RefreshAsync();
        releaseFirstResponse.SetResult();
        await obsoleteRefresh;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(published, Is.EqualTo(["current"]));
            Assert.That(cache.Entry?.Catalog, Is.EqualTo("current"));
            Assert.That(requestCount, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task Refresh_WhenPreparationRejectsCatalog_DoesNotCommitCacheOrValidator()
    {
        using var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("invalid"),
            Headers = { ETag = new EntityTagHeaderValue("\"invalid\"") }
        }));
        var previous = new ModelCatalogCacheEntry<string>(1, "\"previous\"", "previous");
        var cache = new MemoryCacheStore(previous);
        using var coordinator = new ProbeProvider(
            new ProbeClient(new ClientFactory(handler)),
            cache,
            [],
            catalog => catalog == "invalid" ? throw new InvalidDataException("Invalid catalog.") : catalog);

        await coordinator.RestoreCacheAsync();
        await coordinator.RefreshAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Entry, Is.SameAs(previous));
            Assert.That(cache.SaveCount, Is.Zero);
        }
    }

    [Test]
    public async Task Refresh_WhenCatalogSubscriberThrows_AcceptsCatalogAndNotifiesOtherSubscribers()
    {
        using var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("current"),
            Headers = { ETag = new EntityTagHeaderValue("\"current\"") }
        }));
        var cache = new MemoryCacheStore();
        var published = new List<string>();
        using var provider = new ProbeProvider(new ProbeClient(new ClientFactory(handler)), cache, published);
        var secondSubscriberCalls = 0;
        provider.CatalogChanged += (_, _) => throw new InvalidOperationException("Broken subscriber.");
        provider.CatalogChanged += (_, _) => secondSubscriberCalls++;

        await provider.RefreshAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(published, Is.EqualTo(["current"]));
            Assert.That(cache.Entry?.Catalog, Is.EqualTo("current"));
            Assert.That(cache.Entry?.EntityTag, Is.EqualTo("\"current\""));
            Assert.That(secondSubscriberCalls, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Invalidate_WhenObsoleteRequestFails_DoesNotHandleStaleFailure()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DelegateHandler(async (_, _) =>
        {
            requestStarted.SetResult();
            await releaseFailure.Task;
            throw new HttpRequestException("obsolete failure");
        });
        var handledFailures = 0;
        using var coordinator = new ProbeProvider(
            new ProbeClient(new ClientFactory(handler)),
            new MemoryCacheStore(),
            [],
            handleFailure:
            _ =>
            {
                handledFailures++;
                return true;
            });

        var refresh = coordinator.RefreshAsync();
        await requestStarted.Task;
        await coordinator.InvalidateAsync();
        releaseFailure.SetResult();
        await refresh;

        Assert.That(handledFailures, Is.Zero);
    }

    [Test]
    public async Task Fetch_WhenModified_SendsValidatorAndReturnsCatalogWithEntityTag()
    {
        using var handler = new DelegateHandler((request, _) =>
        {
            Assert.That(request.Headers.IfNoneMatch.Single().Tag, Is.EqualTo("\"previous\""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("catalog"),
                Headers = { ETag = new EntityTagHeaderValue("\"current\"") }
            });
        });
        var client = new ProbeClient(new ClientFactory(handler));

        var result = await client.FetchAsync("\"previous\"");

        Assert.That(result, Is.EqualTo(new ModelCatalogFetchResult<string>.Modified("catalog", "\"current\"")));
        Assert.That(client.ReadCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Fetch_WhenNotModified_DoesNotReadSourceResponse()
    {
        using var handler = new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)));
        var client = new ProbeClient(new ClientFactory(handler));

        var result = await client.FetchAsync("\"current\"");

        Assert.That(result, Is.TypeOf<ModelCatalogFetchResult<string>.NotModified>());
        Assert.That(client.ReadCount, Is.Zero);
    }

    [Test]
    public void Fetch_WhenResponseExceedsSourceLimit_RejectsBeforeParsing()
    {
        using var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("catalog")
        }));
        var client = new ProbeClient(new ClientFactory(handler), maximumResponseBytes: 4);

        Assert.That(async () => await client.FetchAsync(), Throws.TypeOf<InvalidDataException>());
        Assert.That(client.ReadCount, Is.Zero);
    }

    [Test]
    public void Fetch_WhenSourceRejectsResponse_PreservesSourceErrorSemantics()
    {
        using var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("source error")
        }));
        var client = new ProbeClient(new ClientFactory(handler), rejectResponse: true);

        Assert.That(async () => await client.FetchAsync(), Throws.TypeOf<ProbeException>());
        Assert.That(client.ReadCount, Is.EqualTo(1));
    }

    private sealed class ProbeClient(
        IHttpClientFactory httpClientFactory,
        long? maximumResponseBytes = null,
        bool rejectResponse = false)
        : ModelCatalogHttpClient<string>(httpClientFactory)
    {
        public int ReadCount { get; private set; }

        protected override long? MaximumResponseBytes => maximumResponseBytes;

        protected override HttpRequestMessage CreateRequest() => new(HttpMethod.Get, "https://example.test/models");

        protected override string ReadResponse(HttpResponseMessage response, ReadOnlySpan<byte> content)
        {
            ReadCount++;
            if (rejectResponse) throw new ProbeException();
            return System.Text.Encoding.UTF8.GetString(content);
        }
    }

    private sealed class ProbeException : Exception;

    private sealed class ProbeProvider(
        ProbeClient client,
        IModelCatalogCacheStore<string> cache,
        List<string> published,
        Func<string, string>? prepare = null,
        Func<Exception, bool>? handleFailure = null)
        : ModelCatalogProvider<string, string>(client, cache, NullLogger.Instance)
    {
        public event EventHandler? CatalogChanged;

        protected override int CacheVersion => 1;

        protected override string PrepareCatalog(string catalog) => prepare?.Invoke(catalog) ?? catalog;

        protected override void ApplyCatalog(string catalog, bool isAuthoritative) => published.Add(catalog);

        protected override void ClearPublishedCatalog() => published.Clear();

        protected override void OnCatalogChanged() => RaiseCatalogChanged(CatalogChanged);

        protected override bool HandleFinalFailure(Exception exception) => handleFailure?.Invoke(exception) ?? false;
    }

    private sealed class MemoryCacheStore(ModelCatalogCacheEntry<string>? entry = null) : IModelCatalogCacheStore<string>
    {
        public ModelCatalogCacheEntry<string>? Entry { get; private set; } = entry;

        public int SaveCount { get; private set; }

        public ValueTask<ModelCatalogCacheEntry<string>?> LoadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Entry);

        public ValueTask SaveAsync(ModelCatalogCacheEntry<string> entry, CancellationToken cancellationToken)
        {
            SaveCount++;
            Entry = entry;
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken)
        {
            Entry = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingCacheStore : IModelCatalogCacheStore<string>
    {
        public ModelCatalogCacheEntry<string>? Entry { get; private set; }

        public int ClearCount { get; private set; }

        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ModelCatalogCacheEntry<string>?> LoadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Entry);

        public async ValueTask SaveAsync(
            ModelCatalogCacheEntry<string> entry,
            CancellationToken cancellationToken)
        {
            SaveStarted.SetResult();
            await ReleaseSave.Task.WaitAsync(cancellationToken);
            Entry = entry;
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken)
        {
            ClearCount++;
            Entry = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
