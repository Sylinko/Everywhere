using System.ComponentModel;
using System.Net;
using System.Reactive.Disposables;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DynamicData;
using Everywhere.AI;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Extensions;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Cloud;

public sealed partial class OfficialModelProvider : ObservableObject, IOfficialModelProvider, IDisposable
{
    public IReadOnlyBindableList<ModelDefinitionTemplate> ModelDefinitions { get; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    private readonly PersistentState _persistentState;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OfficialModelProvider> _logger;
    private readonly SourceList<ModelDefinitionTemplate> _modelDefinitionsSource = new();
    private readonly CompositeDisposable _disposables = new(3);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTimeOffset _nextFetchCooldownTime = DateTimeOffset.MinValue;

    public OfficialModelProvider(
        PersistentState persistentState,
        ICloudClient cloudClient,
        IHttpClientFactory httpClientFactory,
        ILogger<OfficialModelProvider> logger)
    {
        _persistentState = persistentState;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        ModelDefinitions = _modelDefinitionsSource.Connect().BindEx(_disposables);
        _disposables.Add(_modelDefinitionsSource);

        if (persistentState.OfficialModelDefinitionTemplate is not null)
        {
            _modelDefinitionsSource.AddRange(persistentState.OfficialModelDefinitionTemplate);
        }

        WeakReferenceMessenger.Default.RegisterAll(this);

        cloudClient.PropertyChanged += HandleCloudClientPropertyChanged;
        _disposables.Add(Disposable.Create(() => cloudClient.PropertyChanged -= HandleCloudClientPropertyChanged));

        void HandleCloudClientPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ICloudClient.UserProfile) or nameof(ICloudClient.Subscription))
            {
                RefreshAsync().Detach();
            }
        }
    }

    /// <summary>
    /// Actually performs the refresh by calling the official API, parsing the response, and updating the internal list and cache.
    /// </summary>
    /// <param name="exceptionHandler"></param>
    /// <param name="cancellationToken"></param>
    public async Task RefreshAsync(IExceptionHandler? exceptionHandler = null, CancellationToken cancellationToken = default)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken)) return;

        try
        {
            if (CloudConstants.AIGatewayBaseUrl.IsNullOrEmpty()) return;

            IsBusy = true;
            if (DateTimeOffset.Now < _nextFetchCooldownTime)
            {
                // Enforce a cooldown between fetches to avoid hammering the endpoint.
                await Task.Delay(1000, cancellationToken);
                return;
            }

            using var httpClient = _httpClientFactory.CreateClient(nameof(ICloudClient));
            var request = new HttpRequestMessage(HttpMethod.Get, $"{CloudConstants.AIGatewayBaseUrl}/v1/models");

            // If not login, a UserNotLoginException will be thrown
            var response = await httpClient.SendAsync(request, cancellationToken);
            var payload = await ApiPayload<IReadOnlyList<CloudModelDefinition>>.EnsureSuccessFromHttpResponseJsonAsync(
                response,
                ModelsResponseJsonSerializerContext.Default.Options,
                cancellationToken);

            var cloudModelDefinitions = payload.EnsureData();
            var result = cloudModelDefinitions.AsValueEnumerable().Select(m => m.ToModelDefinitionTemplate()).ToList();
            _modelDefinitionsSource.Reset(result);
            _persistentState.OfficialModelDefinitionTemplate = result;

            _nextFetchCooldownTime = DateTimeOffset.Now.AddSeconds(10);
        }
        catch (UserNotLoginException)
        {
            _modelDefinitionsSource.Clear();
            _nextFetchCooldownTime = DateTimeOffset.Now;
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (HttpRequestException ex)
        {
            exceptionHandler?.HandleException(HandledSystemException.Handle(ex));

            if (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _nextFetchCooldownTime = DateTimeOffset.Now.AddMinutes(1);
            }
            else
            {
                // Avoid hammering the endpoint on failure, but allow retries sooner than the normal 10s.
                _nextFetchCooldownTime = DateTimeOffset.Now.AddSeconds(3);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing model definitions");

            exceptionHandler?.HandleException(HandledSystemException.Handle(ex));
            _nextFetchCooldownTime = DateTimeOffset.Now.AddSeconds(3);
        }
        finally
        {
            IsBusy = false;
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        _disposables.Dispose();
        _refreshLock.Dispose();
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    /// <summary>
    /// Standard model definition according to https://models.dev/api.json
    /// </summary>
    /// <param name="ModelId"></param>
    /// <param name="Name"></param>
    /// <param name="SupportsToolCall"></param>
    /// <param name="Modalities"></param>
    /// <param name="LimitInfo"></param>
    private sealed record CloudModelDefinition(
        [property: JsonPropertyName("id")] string ModelId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("icon")] string Icon,
        [property: JsonPropertyName("description")] JsonDynamicResourceKey? DescriptionKey,
        [property: JsonPropertyName("toolCall")] bool SupportsToolCall,
        [property: JsonPropertyName("knowledge")] string? KnowledgeCutoff,
        [property: JsonPropertyName("releaseDate")] string? ReleaseDate,
        [property: JsonPropertyName("deprecationDate")] string? DeprecationDate,
        [property: JsonPropertyName("modalities")] CloudModelModalities Modalities,
        [property: JsonPropertyName("specializations")] IReadOnlyList<string>? Specializations,
        [property: JsonPropertyName("limit")] CloudModelLimitInfo LimitInfo,
        [property: JsonPropertyName("pricing")] CloudModelPricing Pricing,
        [property: JsonPropertyName("quotaLimited")] bool IsQuotaLimited
    )
    {
        public ModelDefinitionTemplate ToModelDefinitionTemplate() =>
            new()
            {
                ModelId = ModelId,
                Name = Name,
                SupportsToolCall = SupportsToolCall,
                KnowledgeCutoff = DateOnly.TryParse(KnowledgeCutoff, out var knowledgeDate) ? knowledgeDate : null,
                ReleaseDate = DateOnly.TryParse(ReleaseDate, out var releaseDate) ? releaseDate : null,
                DeprecationDate = DateOnly.TryParse(DeprecationDate, out var deprecationDate) ? deprecationDate : null,
                InputModalities = ConvertModalities(Modalities.Input),
                OutputModalities = ConvertModalities(Modalities.Output),
                Specializations = ConvertSpecializations(Specializations),
                ContextLimit = LimitInfo.Context,
                OutputLimit = LimitInfo.Output,
                IconUrl = Icon,
                DescriptionKey = DescriptionKey,
                Pricing = ConvertPricing(Pricing),
                IsQuotaLimited = IsQuotaLimited
            };

        private static Modalities ConvertModalities(IReadOnlyList<string> modalityStrings) => modalityStrings.AsValueEnumerable().Aggregate(
            AI.Modalities.None,
            (current, modality) => current | modality.ToLower() switch
            {
                "text" => AI.Modalities.Text,
                "image" => AI.Modalities.Image,
                "audio" => AI.Modalities.Audio,
                "video" => AI.Modalities.Video,
                "pdf" => AI.Modalities.Pdf,
                _ => AI.Modalities.None
            });

        private static ModelSpecializations ConvertSpecializations(IReadOnlyList<string>? specializationStrings)
        {
            if (specializationStrings is null) return ModelSpecializations.Default;

            return specializationStrings.AsValueEnumerable().Aggregate(
                ModelSpecializations.Default,
                (current, specialization) => current | specialization.ToLower() switch
                {
                    "title-generation" => ModelSpecializations.TitleGeneration,
                    "context-compression" => ModelSpecializations.ContextCompression,
                    "image-understanding" => ModelSpecializations.ImageUnderstanding,
                    _ => ModelSpecializations.Default
                });
        }

        private static ModelPricing ConvertPricing(CloudModelPricing pricing)
        {
            const double CreditsMultiplier = 0.01d; // Convert from "per MTokens" to "per Token"
            var tiers = pricing.AsValueEnumerable().Select(t => new PricingTier(
                t.Threshold,
                new TokenPricing(
                    t.Pricing.Input * CreditsMultiplier,
                    t.Pricing.Output * CreditsMultiplier,
                    t.Pricing.CachedInput * CreditsMultiplier))).ToList();
            return new ModelPricing(tiers, ModelPricingUnit.MCreditPerMToken);
        }
    }

    private sealed record CloudModelModalities(
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("output")] IReadOnlyList<string> Output
    );

    private sealed record CloudModelLimitInfo(
        [property: JsonPropertyName("context")] int Context,
        [property: JsonPropertyName("input")] int Input = 0,
        [property: JsonPropertyName("output")] int Output = 0
    );

    private sealed record CloudTokenPricing(
        [property: JsonPropertyName("input")] long Input,
        [property: JsonPropertyName("output")] long Output,
        [property: JsonPropertyName("cachedInput")] long CachedInput
    );

    private sealed record CloudPricingTier(
        [property: JsonPropertyName("threshold")] long Threshold,
        [property: JsonPropertyName("pricing")] CloudTokenPricing Pricing
    );

    private sealed class CloudModelPricing : List<CloudPricingTier>;

    [JsonSerializable(typeof(ApiPayload<IReadOnlyList<CloudModelDefinition>>))]
    private sealed partial class ModelsResponseJsonSerializerContext : JsonSerializerContext;
}