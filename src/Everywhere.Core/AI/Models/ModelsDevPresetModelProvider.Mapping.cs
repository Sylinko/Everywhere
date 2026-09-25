using System.Globalization;
using Everywhere.Collections;

namespace Everywhere.AI;

partial class ModelsDevPresetModelProvider
{
    private static PreparedCatalog ConvertCatalog(Dictionary<string, ModelsDevProvider> providers)
    {
        var result = new Dictionary<string, PreparedProvider>(StringComparer.Ordinal);
        foreach (var local in PresetModelTemplates.Providers)
        {
            if (!providers.TryGetValue(local.Id, out var provider))
            {
                result[local.Id] = new PreparedProvider(PresetModelSourceStatus.Missing, [], new HashSet<string>(StringComparer.Ordinal));
                continue;
            }
            if (provider.Models is null)
            {
                result[local.Id] = new PreparedProvider(PresetModelSourceStatus.Unmappable, [], new HashSet<string>(StringComparer.Ordinal));
                continue;
            }

            var definitions = new List<ModelDefinitionTemplate>();
            var seenModels = new HashSet<string>(StringComparer.Ordinal);
            var knownIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (entryId, source) in provider.Models)
            {
                knownIds.Add(entryId);
                if (source.Id is { } id) knownIds.Add(id);
                var model = ConvertModel(source, local);
                if (model is null || !seenModels.Add(model.ModelId)) continue;
                definitions.Add(model);
            }

            if (definitions.Count == 0)
            {
                // An explicit empty model map is authoritative; entries all rejected as malformed are not.
                var status = provider.Models.Count == 0 ? PresetModelSourceStatus.Available : PresetModelSourceStatus.Unmappable;
                result[local.Id] = new PreparedProvider(status, [], knownIds);
                continue;
            }

            definitions.Sort(ModelDefinitionComparer.Shared);
            if (!definitions.Any(model => model.IsDefault)) definitions[0] = definitions[0] with { IsDefault = true };

            result[local.Id] = new PreparedProvider(PresetModelSourceStatus.Available, definitions, knownIds);
        }

        return result.Values.All(static provider => provider.SourceStatus is PresetModelSourceStatus.Missing or PresetModelSourceStatus.Unmappable) ?
            throw new InvalidDataException("No supported chat models in catalog.") :
            new PreparedCatalog(result);
    }

    private static ModelDefinitionTemplate? ConvertModel(ModelsDevModel value, ModelProviderTemplate local)
    {
        var id = value.Id;
        var context = value.Limit?.Context ?? 0;
        var maxOutput = value.Limit?.Output ?? 0;
        var output = ReadModalities(value.Modalities?.Output ?? default);
        // Output modalities describe capabilities, not mandatory response formats.
        if (string.IsNullOrWhiteSpace(id) || !output.HasFlag(Modalities.Text) || context <= 0 || maxOutput <= 0)
        {
            return null;
        }

        var policy = local.ModelDefinitions.FirstOrDefault(model => model.ModelId == id);
        return new ModelDefinitionTemplate
        {
            ModelId = id,
            Name = value.Name ?? id,
            SupportsToolCall = value.ToolCall,
            InputModalities = ReadModalities(value.Modalities?.Input ?? default),
            OutputModalities = output,
            ContextLimit = context,
            OutputLimit = maxOutput,
            KnowledgeCutoff = Date(value.Knowledge),
            ReleaseDate = Date(value.ReleaseDate),
            DeprecationDate = policy?.DeprecationDate,
            Specializations = policy?.Specializations ?? ModelSpecializations.Default,
            IsDefault = policy?.IsDefault ?? false,
            DescriptionKey = value.Description is { } description ? new DirectLocaleKey(description) : null,
            Pricing = ReadPricing(value.Cost),
            ReasoningEffortValues = new(ReadReasoningEffortValues(value.ReasoningOptions))
        };
    }

    private static string[]? ReadReasoningEffortValues(ValueArray<ModelsDevReasoningOption> options)
    {
        string[]? result = null;
        foreach (var option in options)
        {
            if (option.Type != "effort") continue;

            var values = option.Values
                .AsValueEnumerable()
                .OfType<string>()
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (values.Length == 0) continue;

            if (result is null) result = values;
            else if (!result.SequenceEqual(values, StringComparer.Ordinal)) return null;
        }

        return result;
    }

    private static ModelPricing? ReadPricing(ModelsDevCost? cost)
    {
        if (cost is null) return null;

        var tiers = new List<PricingTier> { new(0, Tokens(cost.Input, cost.Output, cost.CacheRead)) };
        if (cost.Tiers is { } sourceTiers)
        {
            foreach (var tier in sourceTiers)
                if (tier.Tier is { Type: "context", Size: > 0 } boundary)
                    tiers.Add(new PricingTier(boundary.Size, Tokens(tier.Input, tier.Output, tier.CacheRead)));
        }
        else if (cost.ContextOver200K is { } legacy)
        {
            tiers.Add(new PricingTier(200000, Tokens(legacy.Input, legacy.Output, legacy.CacheRead)));
        }

        return new ModelPricing(tiers.OrderBy(tier => tier.Threshold).ToArray(), ModelPricingUnit.UsdPerMToken);
    }

    // NaN represents unknown pricing and must not be shown as free usage.
    private static TokenPricing Tokens(double? input, double? output, double? cacheRead) =>
        new(input ?? double.NaN, output ?? double.NaN, cacheRead ?? double.NaN);

    private static DateOnly? Date(string? value)
    {
        if (value is { Length: 7 }) value += "-01";
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    private static Modalities ReadModalities(ValueArray<string> values)
    {
        return values.AsValueEnumerable().Aggregate(
            Modalities.None,
            (current, value) => current | value switch
            {
                "text" => Modalities.Text,
                "image" => Modalities.Image,
                "audio" => Modalities.Audio,
                "video" => Modalities.Video,
                "pdf" => Modalities.Pdf,
                _ => Modalities.None
            });
    }
}