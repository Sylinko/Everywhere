using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.AI;
using Everywhere.AI.Configurator;

namespace Everywhere.Core.Tests;

/// <summary>
/// Compares local ModelProviderTemplates against models.dev to detect configuration drift.
/// Only checks providers and models that exist on both sides.
/// </summary>
public class PresetModelProviderTemplatesTest
{
    private static readonly Dictionary<string, string> ProviderMapping = new()
    {
        ["openai"] = "openai",
        ["anthropic"] = "anthropic",
        ["google"] = "google",
        ["deepseek"] = "deepseek",
        ["moonshot"] = "moonshotai-cn",
        ["minimax"] = "minimax-cn",
        ["openrouter"] = "openrouter",
        ["siliconcloud"] = "siliconflow-cn",
        // ollama is not included
    };

    private static HttpClient? _httpClient;
    private static Dictionary<string, ApiProvider>? _apiData;

    [OneTimeSetUp]
    public async Task FetchApiData()
    {
        _httpClient = new HttpClient();
        var response = await _httpClient.GetAsync("https://models.dev/api.json");
        Assert.That(response.IsSuccessStatusCode, Is.True, "Failed to fetch models.dev API");

        _apiData = await response.Content.ReadFromJsonAsync<Dictionary<string, ApiProvider>>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.That(_apiData, Is.Not.Null, "Failed to deserialize models.dev API response");
    }

    [OneTimeTearDown]
    public void Cleanup()
    {
        _httpClient?.Dispose();
    }

    [Test]
    public void ModelProviderTemplates_ShouldMatchModelsDevApi()
    {
        var drifts = new List<string>();
        var missing = new List<string>();

        foreach (var provider in PresetBasedAssistantConfigurator.ModelProviderTemplates)
        {
            if (!ProviderMapping.TryGetValue(provider.Id, out var apiProviderKey))
                continue; // Skip providers not tracked

            if (apiProviderKey.Contains(':'))
                apiProviderKey = apiProviderKey.Split(':')[0]; // stepfun/step-3.5-flash:free -> stepfun/step-3.5-flash

            if (!_apiData!.TryGetValue(apiProviderKey, out var apiProvider))
            {
                missing.Add($"Provider '{provider.Id}' not found in models.dev as '{apiProviderKey}'");
                continue;
            }

            foreach (var model in provider.ModelDefinitions)
            {
                var modelKey = model.ModelId;

                if (!apiProvider.Models.TryGetValue(modelKey, out var remote))
                {
                    missing.Add($"{provider.Id}/{modelKey}");
                    continue;
                }

                var diff = CompareModel(model, remote);
                if (diff.Count > 0)
                {
                    drifts.Add($"\n  {provider.Id}/{modelKey}:\n    {string.Join("\n    ", diff)}");
                }
            }
        }

        if (missing.Count > 0)
        {
            TestContext.Out.WriteLine($"Models not found in API:\n  {string.Join("\n  ", missing)}");
        }

        Assert.That(drifts, Is.Empty,
            $"Config drift detected:{string.Join("", drifts)}");
    }

    private static List<string> CompareModel(ModelDefinitionTemplate local, ApiModelInfo remote)
    {
        var diff = new List<string>();

        // Boolean: reasoning
        if (remote.Reasoning is not null && local.SupportsReasoning != remote.Reasoning.Value)
            diff.Add($"SupportsReasoning: {local.SupportsReasoning}, Expected {remote.Reasoning.Value}");

        // Boolean: tool_call
        if (remote.ToolCall is not null && local.SupportsToolCall != remote.ToolCall.Value)
            diff.Add($"SupportsToolCall: {local.SupportsToolCall}, Expected {remote.ToolCall.Value}");

        // Modalities
        if (remote.Modalities is not null)
        {
            var localInput = ModalitiesToSortedString(local.InputModalities);
            var remoteInput = string.Join(",", remote.Modalities.Input.Order());
            if (localInput != remoteInput)
                diff.Add($"InputModalities: [{localInput}], Expected [{remoteInput}]");

            var localOutput = ModalitiesToSortedString(local.OutputModalities);
            var remoteOutput = string.Join(",", remote.Modalities.Output.Order());
            if (localOutput != remoteOutput)
                diff.Add($"OutputModalities: [{localOutput}], Expected [{remoteOutput}]");
        }

        // Limits
        if (remote.Limit is not null)
        {
            if (remote.Limit.Context is not null && local.ContextLimit != remote.Limit.Context.Value)
                diff.Add($"ContextLimit: {local.ContextLimit}, Expected {remote.Limit.Context.Value}");

            if (remote.Limit.Output is not null && local.OutputLimit != remote.Limit.Output.Value)
                diff.Add($"OutputLimit: {local.OutputLimit}, Expected {remote.Limit.Output.Value}");
        }

        return diff;
    }

    private static string ModalitiesToSortedString(Modalities modalities)
    {
        var list = new List<string>();
        if (modalities.HasFlag(Modalities.Audio)) list.Add("audio");
        if (modalities.HasFlag(Modalities.Image)) list.Add("image");
        if (modalities.HasFlag(Modalities.Pdf)) list.Add("pdf");
        if (modalities.HasFlag(Modalities.Text)) list.Add("text");
        if (modalities.HasFlag(Modalities.Video)) list.Add("video");
        return string.Join(",", list);
    }

    #region API DTOs

    private sealed record ApiProvider
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("models")]
        public Dictionary<string, ApiModelInfo> Models { get; init; } = new();
    }

    private sealed record ApiModelInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("reasoning")]
        public bool? Reasoning { get; init; }

        [JsonPropertyName("tool_call")]
        public bool? ToolCall { get; init; }

        [JsonPropertyName("temperature")]
        public bool? Temperature { get; init; }

        [JsonPropertyName("knowledge")]
        public string? Knowledge { get; init; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; init; }

        [JsonPropertyName("last_updated")]
        public string? LastUpdated { get; init; }

        [JsonPropertyName("deprecation_date")]
        public string? DeprecationDate { get; init; }

        [JsonPropertyName("modalities")]
        public ApiModalities? Modalities { get; init; }

        [JsonPropertyName("limit")]
        public ApiLimit? Limit { get; init; }
    }

    private sealed record ApiModalities
    {
        [JsonPropertyName("input")]
        public string[] Input { get; init; } = [];

        [JsonPropertyName("output")]
        public string[] Output { get; init; } = [];
    }

    private sealed record ApiLimit
    {
        [JsonPropertyName("context")]
        public int? Context { get; init; }

        [JsonPropertyName("input")]
        public int? Input { get; init; }

        [JsonPropertyName("output")]
        public int? Output { get; init; }
    }

    #endregion
}