using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Everywhere.Chat;
using Everywhere.Configuration;
using Everywhere.StrategyEngine;

namespace Everywhere.TextSelection;

/// <summary>
/// Resolves registered defaults and applies toolbar ordering without changing strategies offered by the chat window.
/// </summary>
/// <remarks>
/// The toolbar and its settings editor call this service on the UI thread. Default entries are added
/// lazily, after SettingsEngine has loaded the persisted collection.
/// </remarks>
public sealed class TextSelectionToolbarActionsManager : IDisposable
{
    public ObservableCollection<TextSelectionToolbarAction> Items { get; }

    private const string DefaultBuiltInPrefix = "builtin.text-selection.";

    private readonly IStrategyEngine _strategyEngine;
    private readonly IReadOnlyDictionary<string, Strategy> _registeredStrategies;

    public TextSelectionToolbarActionsManager(Settings settings, IStrategyEngine strategyEngine)
    {
        _strategyEngine = strategyEngine;
        _registeredStrategies = strategyEngine.Registry.GetRegisteredStrategies()
            .AsValueEnumerable()
            .OrderByDescending(strategy => strategy.Priority)
            .ToDictionary(strategy => strategy.ConfigurationId, StringComparer.Ordinal);
        Items = settings.TextSelection.ToolbarActions;
        Items.CollectionChanged += HandleItemsChanged;
        ResolveDefaults(Items);
    }

    /// <summary>
    /// Seeds registered text-selection actions while preserving configured order and custom actions.
    /// </summary>
    public void EnsureBuiltInActions()
    {
        var configuredIds = Items.OfType<BuiltInTextSelectionToolbarAction>()
            .Select(action => action.StrategyId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var strategy in _registeredStrategies.Values)
        {
            if (!strategy.ConfigurationId.StartsWith(DefaultBuiltInPrefix, StringComparison.Ordinal)) continue;

            if (configuredIds.Add(strategy.ConfigurationId))
            {
                Items.Add(new BuiltInTextSelectionToolbarAction { StrategyId = strategy.ConfigurationId });
            }
        }
    }

    /// <summary>
    /// Matches a borrowed selection and applies configured ordering and overrides without truncation.
    /// </summary>
    public IReadOnlyList<TextSelectionToolbarItem> GetMatchingItems(TextSelectionAttachment selection)
    {
        if (string.IsNullOrEmpty(selection.Text)) return [];

        EnsureBuiltInActions();

        var context = StrategyContext.FromAttachments([selection]);
        var matchingStrategies = _strategyEngine.GetStrategies(context)
            .AsValueEnumerable()
            .ToDictionary(strategy => strategy.ConfigurationId, StringComparer.Ordinal);
        var results = new List<TextSelectionToolbarItem>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var action in Items)
        {
            if (!action.IsEnabled) continue;

            var strategy = action.CreateStrategy(matchingStrategies);
            if (strategy is null || !seenIds.Add(strategy.Id)) continue;

            results.Add(new TextSelectionToolbarItem(action, strategy));
        }

        return results;
    }

    /// <inheritdoc />
    public void Dispose() => Items.CollectionChanged -= HandleItemsChanged;

    private void HandleItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Serialized polymorphic entries can be replaced by settings import or reload. Resolve them
        // at the collection boundary so every consumer sees the same directly bindable metadata.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ResolveDefaults(Items);
        }
        else if (e.NewItems is { } items)
        {
            ResolveDefaults(items.Cast<TextSelectionToolbarAction>());
        }
    }

    private void ResolveDefaults(IEnumerable<TextSelectionToolbarAction> actions)
    {
        foreach (var action in actions.OfType<BuiltInTextSelectionToolbarAction>())
        {
            action.DefaultStrategy = _registeredStrategies.GetValueOrDefault(action.StrategyId);
        }
    }
}