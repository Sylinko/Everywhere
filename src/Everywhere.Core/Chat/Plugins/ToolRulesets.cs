using Everywhere.Collections;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Evaluates rule sources in order, allowing each later source to override decisions made earlier.
/// </summary>
public sealed class ToolRulesetsPipeline : IToolRulesets
{
    private readonly IReadOnlyList<IToolRulesets> _sources;

    public ToolRulesetsPipeline(IEnumerable<IToolRulesets?> sources)
    {
        _sources = [.. sources.Where(static source => source is not null).Cast<IToolRulesets>()];
    }

    public bool? GetPluginRule(ChatPlugin plugin) =>
        _sources.AsValueEnumerable().Aggregate(null, (bool? current, IToolRulesets source) => source.GetPluginRule(plugin) ?? current);

    public bool? GetFunctionRule(ChatPlugin plugin, ChatFunction function) =>
        _sources.AsValueEnumerable().Aggregate(null, (bool? current, IToolRulesets source) => source.GetFunctionRule(plugin, function) ?? current);
}

/// <summary>
/// Stores exact plugin and function rules in a JSON-friendly observable dictionary.
/// </summary>
public class ObservableToolRulesets : ObservableDictionary<string, bool>, IToolRulesets
{
    public ObservableToolRulesets() : base(StringComparer.OrdinalIgnoreCase) { }

    public ObservableToolRulesets(IEnumerable<KeyValuePair<string, bool>> records) : base(records, StringComparer.OrdinalIgnoreCase)
    {
    }

    public bool? GetPluginRule(ChatPlugin plugin) =>
        TryGetValue(ToolSettingsKey.ForPlugin(plugin), out var value) ? value : null;

    public bool? GetFunctionRule(ChatPlugin plugin, ChatFunction function) =>
        TryGetValue(ToolSettingsKey.ForFunction(plugin, function), out var value) ? value : null;
}

/// <summary>
/// Applies the inheritance and editing semantics of persistent approval-bypass rules.
/// </summary>
public static class ToolBypassApprovalPolicy
{
    public static bool BypassesApproval(IToolRulesets rulesets, ChatPlugin plugin, ChatFunction function)
    {
        if (!function.CanBypassApproval) return false;
        return rulesets.GetFunctionRule(plugin, function) ?? rulesets.GetPluginRule(plugin) ?? function.IsDefaultBypassApproval;
    }

    public static void SetPluginRule(ObservableToolRulesets rulesets, ChatPlugin plugin, bool value)
    {
        var pluginKey = ToolSettingsKey.ForPlugin(plugin);
        if (!value)
        {
            rulesets[pluginKey] = false;
            return;
        }

        var functionPrefix = ToolSettingsKey.ForFunctionPrefix(plugin.Key);
        var keysToRemove = new List<string>();
        foreach (var (key, enabled) in rulesets)
        {
            // Granting the whole plugin must make "all tools" literal. New function-level
            // exceptions may still be recorded after the plugin rule has been granted.
            if (!enabled && key.StartsWith(functionPrefix, StringComparison.OrdinalIgnoreCase))
                keysToRemove.Add(key);
        }

        foreach (var key in keysToRemove)
        {
            rulesets.Remove(key);
        }

        rulesets[pluginKey] = true;
    }

    public static void SetFunctionRule(ObservableToolRulesets rulesets, ChatPlugin plugin, ChatFunction function, bool value)
    {
        if (!function.CanBypassApproval) return;

        var key = ToolSettingsKey.ForFunction(plugin, function);
        var baseline = rulesets.GetPluginRule(plugin) ?? function.IsDefaultBypassApproval;
        if (value == baseline)
        {
            rulesets.Remove(key);
        }
        else
        {
            rulesets[key] = value;
        }
    }

    public static void RemovePluginRules(ObservableToolRulesets rulesets, string pluginKey)
    {
        var functionPrefix = ToolSettingsKey.ForFunctionPrefix(pluginKey);
        rulesets.Remove(ToolSettingsKey.ForPlugin(pluginKey));
        var keysToRemove = rulesets.Keys.AsValueEnumerable().Where(k => k.StartsWith(functionPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var key in keysToRemove)
        {
            rulesets.Remove(key);
        }
    }
}

/// <summary>
/// Creates stable exact keys for persisted plugin and function settings.
/// Each typed segment is JSON Pointer escaped, so plugin and function names can never collide.
/// </summary>
public static class ToolSettingsKey
{
    public static string ForPlugin(ChatPlugin plugin) => ForPlugin(plugin.Key);

    public static string ForPlugin(string pluginKey) => $"p:{Escape(pluginKey)}";

    public static string ForFunction(ChatPlugin plugin, ChatFunction function) =>
        ForFunction(plugin.Key, function.KernelFunction.Name);

    public static string ForFunction(string pluginKey, string functionName) =>
        $"f:{Escape(pluginKey)}/{Escape(functionName)}";

    public static string ForFunctionPrefix(string pluginKey) => $"f:{Escape(pluginKey)}/";

    public static string ForPermission(ChatPlugin plugin, ChatFunction function, string? id = null)
    {
        var functionKey = ForFunction(plugin, function);
        return string.IsNullOrEmpty(id) ? functionKey : $"{functionKey}/permission/{Escape(id)}";
    }

    public static string Escape(string value) =>
        value
            .Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal)
            .Replace(":", "~2", StringComparison.Ordinal);
}