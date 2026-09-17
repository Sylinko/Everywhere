using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Microsoft.Extensions.Logging;

namespace Everywhere.AI;

/// <summary>
/// Application-owned catalog. Initialization schedules cache loading and network revalidation without
/// blocking startup. Only validated catalogs may update durable assistant configurations.
/// </summary>
public sealed partial class ModelsDevPresetModelProvider : ObservableObject, IPresetModelProvider, IAsyncInitializer, IDisposable
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Network + 1;
    public bool IsBusy { get; private set; }
    public bool IsValidated { get; private set; }
    public event EventHandler? ModelsChanged;

    private const int CacheVersion = 1;
    private const long MaximumResponseBytes = 32 * 1024 * 1024;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ModelsDevPresetModelProvider> _logger;
    private readonly string _cachePath = Path.Combine(RuntimeConstants.WritableFolderPath, "models-dev.json");
    private readonly Lock _refreshLock = new();
    private Task<Exception?>? _refreshTask;
    private DateTimeOffset _nextFetchAt;
    private bool _cacheLoaded;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyDictionary<string, IReadOnlyList<ModelDefinitionTemplate>> _models;
    private IReadOnlyDictionary<string, IReadOnlyList<ModelDefinitionTemplate>> _validatedModels = new Dictionary<string, IReadOnlyList<ModelDefinitionTemplate>>();
    private CacheEnvelope? _cache;
    private DateTimeOffset _retryAfter;
    private bool _started;
    private bool _disposed;

    public ModelsDevPresetModelProvider(IHttpClientFactory httpClientFactory, ILogger<ModelsDevPresetModelProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _models = PresetModelTemplates.Providers.ToDictionary(p => p.Id, p => p.ModelDefinitions, StringComparer.Ordinal);
    }

    public IReadOnlyList<ModelDefinitionTemplate> GetModelDefinitions(string? providerId) =>
        providerId is not null && Volatile.Read(ref _models).TryGetValue(providerId, out var models) ? models : [];

    public ModelDefinitionTemplate? GetValidatedModel(string? providerId, string? modelId) =>
        providerId is not null && Volatile.Read(ref _validatedModels).TryGetValue(providerId, out var models) ?
            models.FirstOrDefault(m => m.ModelId == modelId) : null;

    public Task InitializeAsync()
    {
        if (_started) return Task.CompletedTask;
        _started = true;
        StartRefresh(retry: true).Detach(_logger.ToExceptionHandler());
        return Task.CompletedTask;
    }

    public async Task RefreshAsync(IExceptionHandler? exceptionHandler = null, CancellationToken cancellationToken = default)
    {
        // A caller can stop waiting without canceling a request shared by other editors.
        try
        {
            var error = await StartRefresh(retry: false).WaitAsync(cancellationToken);
            if (error is not null && exceptionHandler is not null && !_disposed)
                await Dispatcher.UIThread.InvokeAsync(() => exceptionHandler.HandleException(HandledSystemException.Handle(error)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private Task<Exception?> StartRefresh(bool retry)
    {
        lock (_refreshLock)
        {
            if (_disposed) return Task.FromResult<Exception?>(null);
            if (_refreshTask is { IsCompleted: false }) return _refreshTask;
            if (DateTimeOffset.UtcNow < _nextFetchAt) return Task.FromResult<Exception?>(null);
            _refreshTask = Task.Run(() => RefreshCoreAsync(retry, _lifetime.Token));
            return _refreshTask;
        }
    }

    private async Task<Exception?> RefreshCoreAsync(bool retry, CancellationToken token)
    {
        try
        {
            if (!_cacheLoaded)
            {
                await LoadCacheAsync(token);
                _cacheLoaded = true;
            }
            for (var attempt = 0; ; attempt++)
            {
                var wait = _retryAfter - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
                await SetBusyAsync(true);
                try
                {
                    await FetchAsync(token);
                    lock (_refreshLock) _nextFetchAt = DateTimeOffset.UtcNow.AddSeconds(10);
                    return null;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not refresh the preset model catalog");
                    if (!retry || attempt >= 2 || !IsTransient(ex)) return ex;
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1) + Random.Shared.NextDouble());
                    if (_retryAfter < DateTimeOffset.UtcNow + delay) _retryAfter = DateTimeOffset.UtcNow + delay;
                }
                finally { await SetBusyAsync(false); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
    }

    private async Task LoadCacheAsync(CancellationToken token)
    {
        try
        {
            if (!File.Exists(_cachePath) || new FileInfo(_cachePath).Length > MaximumResponseBytes) return;
            await using var stream = File.OpenRead(_cachePath);
            var cache = await JsonSerializer.DeserializeAsync(stream, CacheJsonContext.Default.CacheEnvelope, token);
            if (cache is null || cache.Version != CacheVersion) return;
            var models = ConvertCatalog(cache.Providers);
            _cache = cache;
            await PublishAsync(models, validated: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Ignoring invalid preset model cache");
        }
    }

    private async Task FetchAsync(CancellationToken token)
    {
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://models.dev/api.json");
        if (_cache?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _retryAfter = response.Headers.RetryAfter?.Date ??
                DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
        }
        if (response.StatusCode == HttpStatusCode.NotModified && _cache is not null)
        {
            await PublishAsync(ConvertCatalog(_cache.Providers), validated: true);
            return;
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Model catalog exceeds the size limit.");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var bytes = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(bytes, token);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes) throw new InvalidDataException("Model catalog exceeds the size limit.");
            buffer.Write(bytes, 0, read);
        }
        buffer.Position = 0;
        using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: token);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a provider map.");
        var providers = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var provider in PresetModelTemplates.Providers)
            if (document.RootElement.TryGetProperty(provider.Id, out var value)) providers.Add(provider.Id, value.Clone());
        var models = ConvertCatalog(providers);
        var cache = new CacheEnvelope(CacheVersion, response.Headers.ETag?.ToString(), providers);
        // Commit body and ETag together. Cache write errors do not invalidate usable network data.
        try
        {
            await using (var stream = File.Create(_cachePath + ".tmp"))
                await JsonSerializer.SerializeAsync(stream, cache, CacheJsonContext.Default.CacheEnvelope, token);
            File.Move(_cachePath + ".tmp", _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Could not persist model catalog"); }
        _cache = cache;
        await PublishAsync(models, validated: true);
    }

    private async Task PublishAsync(IReadOnlyDictionary<string, IReadOnlyList<ModelDefinitionTemplate>> models, bool validated)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed) return;
            var merged = new Dictionary<string, IReadOnlyList<ModelDefinitionTemplate>>(_models, StringComparer.Ordinal);
            foreach (var (id, definitions) in models) merged[id] = definitions;
            Volatile.Write(ref _models, merged);
            if (validated) Volatile.Write(ref _validatedModels, models);
            IsValidated = validated;
            OnPropertyChanged(nameof(IsValidated));
            if (ModelsChanged is not { } changed) return;
            foreach (var subscriber in changed.GetInvocationList().Cast<EventHandler>())
            {
                try { subscriber(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.LogError(ex, "Preset catalog subscriber failed"); }
            }
        });
    }

    private async Task SetBusyAsync(bool value) => await Dispatcher.UIThread.InvokeAsync(() =>
    {
        if (_disposed) return;
        IsBusy = value;
        OnPropertyChanged(nameof(IsBusy));
    });

    private static bool IsTransient(Exception ex) => ex is TaskCanceledException || ex is HttpRequestException http &&
        (http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
         http.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ||
         http.StatusCode is null && http.HttpRequestError != HttpRequestError.SecureConnectionError &&
         http.InnerException is not AuthenticationException);

    public void Dispose()
    {
        Task<Exception?>? pending;
        lock (_refreshLock)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _refreshTask;
        }
        _lifetime.Cancel();
        ReleaseLifetimeAsync(pending).Detach(_logger.ToExceptionHandler());
    }

    private async Task ReleaseLifetimeAsync(Task<Exception?>? pending)
    {
        try
        {
            if (pending is not null) await pending;
        }
        finally { _lifetime.Dispose(); }
    }

    private sealed record CacheEnvelope(int Version, string? ETag, Dictionary<string, JsonElement> Providers);

    [JsonSerializable(typeof(CacheEnvelope))]
    private sealed partial class CacheJsonContext : JsonSerializerContext;
}
