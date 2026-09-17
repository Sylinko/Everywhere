using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Everywhere.AI;

partial class ModelsDevPresetModelProvider
{
    private readonly Dictionary<(string Provider, string Model), (string Json, ModelDefinitionTemplate Model)> _parsedModels = [];

    private Dictionary<string, IReadOnlyList<ModelDefinitionTemplate>> ConvertCatalog(Dictionary<string, JsonElement> providers)
    {
        var result = new Dictionary<string, IReadOnlyList<ModelDefinitionTemplate>>(StringComparer.Ordinal);
        foreach (var local in PresetModelTemplates.Providers)
        {
            if (!providers.TryGetValue(local.Id, out var provider) ||
                provider.ValueKind != JsonValueKind.Object ||
                !provider.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Object) continue;

            var definitions = new List<ModelDefinitionTemplate>();
            var sourceJson = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in models.EnumerateObject())
            {
                try
                {
                    var model = ConvertModel(entry.Value, local);
                    if (model is not null && definitions.All(m => m.ModelId != model.ModelId))
                    {
                        definitions.Add(model);
                        sourceJson[model.ModelId] = entry.Value.GetRawText();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
                {
                    _logger.LogDebug(ex, "Ignoring invalid model {Provider}/{Model}", local.Id, entry.Name);
                }
            }
            if (definitions.Count == 0) continue;

            definitions.Sort((a, b) =>
            {
                var byRelease = Nullable.Compare(b.ReleaseDate, a.ReleaseDate);
                if (byRelease != 0) return byRelease;
                var byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.ModelId, b.ModelId);
            });

            if (!definitions.AsValueEnumerable().Any(m => m.IsDefault)) definitions[0] = definitions[0] with { IsDefault = true };

            for (var i = 0; i < definitions.Count; i++)
            {
                var model = definitions[i];
                var key = (local.Id, model.ModelId);
                var json = sourceJson[model.ModelId];
                if (_parsedModels.TryGetValue(key, out var previous) && previous.Json == json && previous.Model.IsDefault == model.IsDefault)
                {
                    definitions[i] = previous.Model;
                }
                else
                {
                    _parsedModels[key] = (json, model);
                }
            }

            foreach (var key in _parsedModels.Keys.Where(k => k.Provider == local.Id && !sourceJson.ContainsKey(k.Model)).ToArray())
            {
                _parsedModels.Remove(key);
            }

            var current = GetModelDefinitions(local.Id);
            result[local.Id] = current.Count == definitions.Count &&
                current.AsValueEnumerable().Zip(definitions).All(pair => ReferenceEquals(pair.First, pair.Second)) ? current : definitions.AsReadOnly();
        }

        return result.Count == 0 ? throw new InvalidDataException("No supported chat models in catalog.") : result;
    }

    private static ModelDefinitionTemplate? ConvertModel(JsonElement value, ModelProviderTemplate local)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;

        var id = Text(value, "id");
        if (string.IsNullOrWhiteSpace(id) || !value.TryGetProperty("modalities", out var modalities) ||
            !value.TryGetProperty("limit", out var limits)) return null;
        var input = ReadModalities(modalities, "input");
        var output = ReadModalities(modalities, "output");
        var context = Integer(limits, "context");
        var maxOutput = Integer(limits, "output");
        // Output modalities describe capabilities, not mandatory output formats.
        // Keep mixed-output models that can produce text; do not infer endpoint
        // compatibility from names, providers or input modalities.
        if (!output.HasFlag(Modalities.Text) || context <= 0 || maxOutput <= 0) return null;
        var policy = local.ModelDefinitions.FirstOrDefault(m => m.ModelId == id);
        return new ModelDefinitionTemplate
        {
            ModelId = id,
            Name = Text(value, "name") ?? id,
            SupportsToolCall = value.TryGetProperty("tool_call", out var tools) && tools.ValueKind == JsonValueKind.True,
            InputModalities = input,
            OutputModalities = output,
            ContextLimit = context,
            OutputLimit = maxOutput,
            KnowledgeCutoff = Date(Text(value, "knowledge")),
            ReleaseDate = Date(Text(value, "release_date")),
            DeprecationDate = policy?.DeprecationDate,
            Specializations = policy?.Specializations ?? ModelSpecializations.Default,
            IsDefault = policy?.IsDefault ?? false,
            DescriptionKey = Text(value, "description") is { } description ? new DirectLocaleKey(description) : null,
            Pricing = ReadPricing(value)
        };
    }

    private static ModelPricing? ReadPricing(JsonElement model)
    {
        if (!model.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object) return null;
        var tiers = new List<PricingTier> { new(0, Tokens(cost)) };
        if (cost.TryGetProperty("tiers", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
                if (value.TryGetProperty("tier", out var tier) && Text(tier, "type") == "context" && Integer(tier, "size") is > 0 and var threshold)
                    tiers.Add(new PricingTier(threshold, Tokens(value)));
        }
        else if (cost.TryGetProperty("context_over_200k", out var legacy)) tiers.Add(new PricingTier(200000, Tokens(legacy)));
        return new ModelPricing(tiers.OrderBy(t => t.Threshold).ToArray(), ModelPricingUnit.UsdPerMToken);
    }

    // NaN preserves unknown cache pricing; it must not be represented as free usage.
    private static TokenPricing Tokens(JsonElement value) => new(Number(value, "input"), Number(value, "output"), Number(value, "cache_read"));
    private static double Number(JsonElement value, string key) => value.TryGetProperty(key, out var number) && number.TryGetDouble(out var result) ? result : double.NaN;
    private static int Integer(JsonElement value, string key) => value.TryGetProperty(key, out var number) && number.TryGetInt32(out var result) ? result : 0;
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
    private static DateOnly? Date(string? value)
    {
        // Month precision denotes the start of that month, never the current day.
        if (value is { Length: 7 }) value += "-01";
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    private static Modalities ReadModalities(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var items) || items.ValueKind != JsonValueKind.Array) return Modalities.None;
        var result = Modalities.None;
        foreach (var item in items.EnumerateArray())
            result |= item.GetString() switch
            {
                "text" => Modalities.Text, "image" => Modalities.Image, "audio" => Modalities.Audio,
                "video" => Modalities.Video, "pdf" => Modalities.Pdf, _ => Modalities.None
            };
        return result;
    }
}
