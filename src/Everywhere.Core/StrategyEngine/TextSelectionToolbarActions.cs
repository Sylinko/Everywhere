using System.Collections.ObjectModel;
using Everywhere.Chat;
using Everywhere.Configuration;
using Everywhere.Interop;
using Lucide.Avalonia;

namespace Everywhere.StrategyEngine;

/// <summary>
/// Applies toolbar-only overrides and ordering without changing strategies offered by the chat window.
/// </summary>
/// <remarks>
/// The toolbar and its settings editor call this service on the UI thread. Default entries are added
/// lazily, after SettingsEngine has loaded the persisted collection.
/// </remarks>
public sealed class TextSelectionToolbarActions(Settings settings, IStrategyEngine strategyEngine)
{
    public ObservableCollection<TextSelectionToolbarAction> Items => settings.TextSelectionToolbar.Actions;

    private const string DefaultBuiltInPrefix = "builtin.text-selection.";

    private readonly IReadOnlyDictionary<string, Strategy> _registeredStrategies = strategyEngine.Registry
        .GetRegisteredStrategies()
        .OrderByDescending(strategy => strategy.Priority)
        .ToDictionary(strategy => strategy.ConfigurationId, StringComparer.Ordinal);

    /// <summary>
    /// Seeds only text-selection actions. Older versions seeded every registered strategy; remove
    /// those extra defaults while preserving custom actions and any explicitly edited entries.
    /// </summary>
    public void EnsureBuiltInActions()
    {
        if (!settings.TextSelectionToolbar.HasMigratedActionDefaults)
        {
            for (var index = Items.Count - 1; index >= 0; index--)
            {
                var action = Items[index];
                if (action.BuiltInId is { } id &&
                    _registeredStrategies.ContainsKey(id) &&
                    !id.StartsWith(DefaultBuiltInPrefix, StringComparison.Ordinal) &&
                    action.Name is null && action.Icon is null && action.Prompt is null)
                {
                    Items.RemoveAt(index);
                }
            }
        }

        var configuredIds = Items
            .Select(action => action.BuiltInId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var strategy in _registeredStrategies.Values)
        {
            if (!strategy.ConfigurationId.StartsWith(DefaultBuiltInPrefix, StringComparison.Ordinal)) continue;

            if (configuredIds.Add(strategy.ConfigurationId))
            {
                Items.Add(new TextSelectionToolbarAction { BuiltInId = strategy.ConfigurationId });
            }
        }

        settings.TextSelectionToolbar.HasMigratedActionDefaults = true;
    }

    public Strategy? GetBuiltInStrategy(TextSelectionToolbarAction action) =>
        action.BuiltInId is { } id ? _registeredStrategies.GetValueOrDefault(id) : null;

    public IReadOnlyList<Strategy> GetStrategies(TextSelectionData selection)
    {
        if (string.IsNullOrEmpty(selection.Text)) return [];

        EnsureBuiltInActions();

        var context = StrategyContext.FromAttachments([new TextSelectionAttachment(selection.Text, selection.Element)]);
        var matchingStrategies = strategyEngine.GetStrategies(context)
            .ToDictionary(strategy => strategy.ConfigurationId, StringComparer.Ordinal);

        // Persisted values bypass the settings UI's range validation.
        var maxActionCount = Math.Clamp(
            settings.TextSelectionToolbar.MaxActionCount,
            TextSelectionToolbarSettings.MinActionCount,
            TextSelectionToolbarSettings.MaxAllowedActionCount);
        var results = new List<Strategy>(maxActionCount);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var action in Items)
        {
            if (!action.IsEnabled) continue;
            var matchedStrategy = action.BuiltInId is { } id ? matchingStrategies.GetValueOrDefault(id) : null;
            var strategy = CreateStrategy(action, matchedStrategy);
            if (strategy is null || !seenIds.Add(strategy.Id)) continue;

            results.Add(strategy);
            if (results.Count == maxActionCount) break;
        }

        return results;
    }

    private static Strategy? CreateStrategy(TextSelectionToolbarAction action, Strategy? matchedStrategy)
    {
        if (action.IsBuiltIn)
        {
            if (matchedStrategy is not { } strategy) return null;

            // Retain matching conditions, preprocessors, system prompts and tool permissions.
            return strategy with
            {
                NameKey = string.IsNullOrWhiteSpace(action.Name) ? strategy.NameKey : new DirectLocaleKey(action.Name.Trim()),
                Icon = action.Icon ?? strategy.Icon,
                Body = string.IsNullOrWhiteSpace(action.Prompt) ? strategy.Body : action.Prompt
            };
        }

        if (string.IsNullOrWhiteSpace(action.Name) || string.IsNullOrWhiteSpace(action.Prompt)) return null;

        return new Strategy
        {
            Id = $"toolbar.{action.Id:D}",
            NameKey = new DirectLocaleKey(action.Name.Trim()),
            Icon = action.Icon ?? LucideIconKind.Sparkles,
            Body = action.Prompt
        };
    }
}
