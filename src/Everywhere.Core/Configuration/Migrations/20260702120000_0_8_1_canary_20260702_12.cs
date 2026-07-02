using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.AI.Prompts.Database;
using Everywhere.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Configuration.Migrations;

/// <summary>
/// Migrates legacy assistant prompt text into Prompt Manager storage.
/// </summary>
/// <remarks>
/// This migration coordinates settings JSON with <c>prompt.db</c>. Database writes happen before
/// the settings file is rewritten, so prompt import is deliberately idempotent: if the JSON write
/// fails after a row is created, the next run finds the existing <see cref="PromptSource.Migration"/>
/// row by normalized body and reuses it.
/// </remarks>
public sealed class _20260702120000_0_8_1_canary_20260702_12(
    IDbContextFactory<PromptDbContext> dbFactory,
    ILogger<_20260702120000_0_8_1_canary_20260702_12> logger) : SettingsMigration
{
    private const string LegacySystemPromptPropertyName = "SystemPrompt";
    private const string SystemPromptIdPropertyName = "SystemPromptId";
    private const int PromptNameLimit = 256;

    public override SemanticVersion Version => new(0, 8, 1, 0, "canary.20260702.12");

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [ImportAssistantPrompts];

    private bool ImportAssistantPrompts(JsonObject root)
    {
        if (GetPathNode(root, "Model.CustomAssistants") is not JsonArray assistants)
        {
            return false;
        }

        var modified = false;
        var promptImports = new List<AssistantPromptImport>();

        foreach (var assistant in assistants.AsValueEnumerable().OfType<JsonObject>())
        {
            if (!assistant.TryGetPropertyValue(LegacySystemPromptPropertyName, out var legacyPromptNode))
            {
                modified |= EnsureSystemPromptId(assistant);
                continue;
            }

            if (!TryReadString(legacyPromptNode, out var legacyPrompt))
            {
                logger.LogWarning("Legacy assistant SystemPrompt had a non-string JSON value and will be mapped to the default prompt.");
                modified |= SetPromptId(assistant, PromptConstants.DefaultPromptId);
                modified |= assistant.Remove(LegacySystemPromptPropertyName);
                continue;
            }

            var normalizedPrompt = NormalizePromptText(legacyPrompt);
            if (string.IsNullOrWhiteSpace(normalizedPrompt) ||
                string.Equals(normalizedPrompt, NormalizePromptText(DefaultPrompts.DefaultSystemPrompt), StringComparison.Ordinal))
            {
                modified |= SetPromptId(assistant, PromptConstants.DefaultPromptId);
                modified |= assistant.Remove(LegacySystemPromptPropertyName);
                continue;
            }

            promptImports.Add(new AssistantPromptImport(
                assistant,
                ReadStringProperty(assistant, "Name"),
                legacyPrompt,
                normalizedPrompt));
        }

        if (promptImports.Count == 0)
        {
            return modified;
        }

        using var dbContext = dbFactory.CreateDbContext();
        var promptIdsByNormalizedTemplate = LoadExistingPromptIds(dbContext);
        var now = DateTimeOffset.UtcNow;

        foreach (var group in promptImports
                     .AsValueEnumerable()
                     .GroupBy(static item => item.NormalizedTemplate, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            if (!promptIdsByNormalizedTemplate.TryGetValue(group.Key, out var promptId))
            {
                var imports = group.ToList();
                promptId = Guid.CreateVersion7();
                dbContext.Prompts.Add(new PromptEntity
                {
                    Id = promptId,
                    Name = CreatePromptName(imports),
                    Template = imports[0].OriginalTemplate,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Source = PromptSource.Migration
                });
                promptIdsByNormalizedTemplate[group.Key] = promptId;
            }

            foreach (var item in group)
            {
                modified |= SetPromptId(item.Assistant, promptId);
                modified |= item.Assistant.Remove(LegacySystemPromptPropertyName);
            }
        }

        dbContext.SaveChanges();
        return modified;
    }

    private static Dictionary<string, Guid> LoadExistingPromptIds(PromptDbContext dbContext)
    {
        var idsByTemplate = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var prompt in dbContext.Prompts
                     .AsNoTracking()
                     .Where(static prompt => prompt.Source == PromptSource.Migration)
                     .Select(static prompt => new { prompt.Id, prompt.Template }))
        {
            idsByTemplate.TryAdd(NormalizePromptText(prompt.Template), prompt.Id);
        }

        return idsByTemplate;
    }

    private static string NormalizePromptText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool EnsureSystemPromptId(JsonObject assistant)
    {
        if (TryReadGuid(assistant, SystemPromptIdPropertyName, out _))
        {
            return false;
        }

        assistant[SystemPromptIdPropertyName] = PromptConstants.DefaultPromptId.ToString("D");
        return true;
    }

    private static bool SetPromptId(JsonObject assistant, Guid promptId)
    {
        if (TryReadGuid(assistant, SystemPromptIdPropertyName, out var existingPromptId) && existingPromptId == promptId)
        {
            return false;
        }

        assistant[SystemPromptIdPropertyName] = promptId.ToString("D");
        return true;
    }

    private static string? CreatePromptName(IReadOnlyList<AssistantPromptImport> imports)
    {
        var assistantNames = imports
            .AsValueEnumerable()
            .Select(static item => item.AssistantName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();

        var name = assistantNames.Count == 1
            ? $"{assistantNames[0]} system prompt"
            : FirstMeaningfulLine(imports[0].OriginalTemplate) ?? "Migrated assistant prompt";

        return name.Length <= PromptNameLimit ? name : name[..PromptNameLimit];
    }

    private static string? FirstMeaningfulLine(string text) =>
        NormalizePromptText(text)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .AsValueEnumerable()
            .FirstOrDefault();

    private static string? ReadStringProperty(JsonObject obj, string propertyName) =>
        obj.TryGetPropertyValue(propertyName, out var node) && TryReadString(node, out var value) ? value : null;

    private static bool TryReadGuid(JsonObject obj, string propertyName, out Guid value)
    {
        value = default;
        return obj.TryGetPropertyValue(propertyName, out var node) && TryReadString(node, out var text) && Guid.TryParse(text, out value);
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is null)
        {
            return false;
        }

        try
        {
            if (node.GetValueKind() is JsonValueKind.Null)
            {
                return false;
            }

            value = node.GetValue<string>();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record AssistantPromptImport(
        JsonObject Assistant,
        string? AssistantName,
        string OriginalTemplate,
        string NormalizedTemplate);
}