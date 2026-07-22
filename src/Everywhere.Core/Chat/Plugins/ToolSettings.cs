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

    public bool? IsPluginAllowed(ChatPlugin plugin) =>
        _sources.AsValueEnumerable().Aggregate(null, (bool? current, IToolRulesets source) => source.IsPluginAllowed(plugin) ?? current);

    public bool? IsFunctionAllowed(ChatPlugin plugin, ChatFunction function) =>
        _sources.AsValueEnumerable().Aggregate(null, (bool? current, IToolRulesets source) => source.IsFunctionAllowed(plugin, function) ?? current);
}

/// <summary>
/// Stores exact tool enablement overrides in a JSON-friendly observable dictionary.
/// </summary>
public class ToolEnablementSettings : ObservableDictionary<string, bool>, IToolRulesets
{
    public ToolEnablementSettings() : base(StringComparer.OrdinalIgnoreCase) { }

    public ToolEnablementSettings(IEnumerable<KeyValuePair<string, bool>> records)
        : base(records, StringComparer.OrdinalIgnoreCase)
    {
    }

    public bool? IsPluginAllowed(ChatPlugin plugin) =>
        TryGetValue(ToolSettingsKey.ForPlugin(plugin), out var value) ? value : null;

    public bool? IsFunctionAllowed(ChatPlugin plugin, ChatFunction function) =>
        TryGetValue(ToolSettingsKey.ForFunction(plugin, function), out var value) ? value : null;
}

/// <summary>
/// Stores exact user overrides for automatic tool-call approval.
/// </summary>
public class ToolAutoApprovalSettings() : ObservableDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

/// <summary>
/// Creates stable exact keys for persisted plugin and function settings.
/// Each typed segment is JSON Pointer escaped, so plugin and function names can never collide.
/// </summary>
public static class ToolSettingsKey
{
    public static string ForPlugin(ChatPlugin plugin) => ForPlugin(plugin.Key);

    public static string ForPlugin(string pluginKey) => $"plugin:{Escape(pluginKey)}";

    public static string ForFunction(ChatPlugin plugin, ChatFunction function) =>
        ForFunction(plugin.Key, function.KernelFunction.Name);

    public static string ForFunction(string pluginKey, string functionName) =>
        $"function:{Escape(pluginKey)}/{Escape(functionName)}";

    public static string ForFunctionPrefix(string pluginKey) => $"function:{Escape(pluginKey)}/";

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