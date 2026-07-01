using System.Text.RegularExpressions;
using Everywhere.Serialization;
using MessagePack;
using ZLinq;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Allowed tool/plugin names or function names with enable/disable flag. The key is in format of "pluginKey" or "pluginKey.functionName", value is whether it's enabled or not.
/// </summary>
/// <remarks>
/// Wildcard is allowed. e.g.
/// { "builtin.visual_tree.*": true, "builtin.web.web_*": true, "builtin.web.web_search": false }
///
/// Note that `builtin.visual_tree.*` and `builtin.visual_tree` are different.
/// Thr former means all functions in `builtin.visual_tree` should be applied (enable or disable) no matter whether then are enabled.
/// But the latter only means the `builtin.visual_tree` should be applied, functions will keep their original state.
///
/// When applying, keys first ordered then apply one by one, latter overrides former.
/// </remarks>
[MessagePackFormatter(typeof(ToolRulesetsMessagePackFormatter))]
public sealed class ToolRulesets : Dictionary<string, bool>
{
    public ToolRulesets() : base(StringComparer.OrdinalIgnoreCase) { }

    public ToolRulesets(int capacity) : base(capacity, StringComparer.OrdinalIgnoreCase) { }

    public ToolRulesets(IDictionary<string, bool> dictionary) : base(dictionary, StringComparer.OrdinalIgnoreCase) { }

    public ToolRulesets Union(ToolRulesets? overrides)
    {
        if (overrides is null) return this;

        var union = new ToolRulesets(this);
        foreach (var kvp in overrides)
        {
            union[kvp.Key] = kvp.Value;
        }

        return union;
    }

    public bool? IsPluginAllowed(ChatPlugin plugin)
    {
        bool? isAllowed = null;
        foreach (var kvp in this.AsValueEnumerable().OrderBy(kvp => kvp.Key))
        {
            var dotIndex = kvp.Key.LastIndexOf('.');
            var pluginPattern = dotIndex < 0 ? kvp.Key : kvp.Key[..dotIndex];

            // Use simple Glob to Regex conversion
            var regexPattern = "^" + Regex.Escape(pluginPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            var pluginRegex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (pluginRegex.IsMatch(plugin.Key))
            {
                if (dotIndex < 0)
                {
                    isAllowed = kvp.Value;
                }
                else if (kvp.Value)
                {
                    // Any rule enabling functions in this plugin forces the plugin to be enabled
                    isAllowed = true;
                }
            }
        }

        return isAllowed;
    }

    public bool? IsFunctionAllowed(ChatPlugin plugin, ChatFunction function)
    {
        bool? isAllowed = null;
        var fullFunctionName = $"{plugin.Key}.{function.KernelFunction.Metadata.Name}";
        foreach (var kvp in this.AsValueEnumerable().OrderBy(kvp => kvp.Key))
        {
            var dotIndex = kvp.Key.LastIndexOf('.');
            if (dotIndex < 0)
            {
                // Rule targets plugin layer
                var pluginRegexPattern = "^" + Regex.Escape(kvp.Key).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                var pluginRegex = new Regex(pluginRegexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                // If plugin is explicitly disabled, the function is as well.
                // Otherwise, we keep its state as is.
                if (pluginRegex.IsMatch(plugin.Name) && !kvp.Value)
                {
                    isAllowed = false;
                }
            }
            else
            {
                // Rule targets function layer
                var functionRegexPattern = "^" + Regex.Escape(kvp.Key).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                var functionRegex = new Regex(functionRegexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (functionRegex.IsMatch(fullFunctionName))
                {
                    isAllowed = kvp.Value;
                }
            }
        }

        return isAllowed;
    }
}

public static class ToolRulesetsExtensions
{
    /// <summary>
    /// Creates a copy of the source ToolRulesets and applies overrides on top. If source is null, returns overrides. If overrides is null, returns a copy of source.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="overrides"></param>
    /// <returns></returns>
    public static ToolRulesets? Copy(this ToolRulesets? source, ToolRulesets? overrides = null)
    {
        if (source is null) return overrides;

        var copy = new ToolRulesets(source);
        if (overrides is null) return copy;

        foreach (var kvp in overrides)
        {
            copy[kvp.Key] = kvp.Value;
        }

        return copy;
    }
}