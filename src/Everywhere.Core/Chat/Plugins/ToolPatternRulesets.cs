using System.IO.Enumeration;
using Everywhere.Serialization;
using MessagePack;
using ZLinq.Linq;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Provides nullable plugin and function rules without assigning domain-specific meaning to their values.
/// A missing rule means the caller should continue with its next source or default.
/// </summary>
public interface IToolRulesets
{
    bool? GetPluginRule(ChatPlugin plugin);

    bool? GetFunctionRule(ChatPlugin plugin, ChatFunction function);
}

/// <summary>
/// Pattern-based function rules grouped by plugin pattern.
/// The outer key only matches <see cref="ChatPlugin.Key"/> and each inner key only matches a function name.
/// </summary>
/// <example>
/// <code>
/// {
///   "builtin.*": {
///     "*": true,
///     "web*": false
///   }
/// }
/// </code>
/// </example>
[MessagePackFormatter(typeof(ToolPatternRulesetsMessagePackFormatter))]
public class ToolPatternRulesets : Dictionary<string, ToolFunctionPatternRulesets>, IToolRulesets
{
    public ToolPatternRulesets() : base(StringComparer.OrdinalIgnoreCase) { }

    public ToolPatternRulesets(int capacity) : base(capacity, StringComparer.OrdinalIgnoreCase) { }

    public ToolPatternRulesets(IDictionary<string, ToolFunctionPatternRulesets> dictionary) : base(dictionary, StringComparer.OrdinalIgnoreCase)
    {
    }

    public ToolPatternRulesets Union(ToolPatternRulesets? overrides)
    {
        if (overrides is null) return CopyCore();

        var result = CopyCore();
        foreach (var (pluginPattern, functionOverrides) in overrides)
        {
            if (!result.TryGetValue(pluginPattern, out var functions))
            {
                result[pluginPattern] = new ToolFunctionPatternRulesets(functionOverrides);
                continue;
            }

            foreach (var (functionPattern, value) in functionOverrides)
            {
                functions[functionPattern] = value;
            }
        }

        return result;
    }

    public bool? GetPluginRule(ChatPlugin plugin)
    {
        bool? result = null;
        foreach (var (_, functions) in GetMatchingPluginRules(plugin.Key))
        {
            if (functions.Values.Any(static value => value))
            {
                result = true;
            }
            else if (functions.TryGetValue("*", out var enabled) && !enabled)
            {
                result = false;
            }
        }

        return result;
    }

    public bool? GetFunctionRule(ChatPlugin plugin, ChatFunction function)
    {
        bool? result = null;
        foreach (var (_, functions) in GetMatchingPluginRules(plugin.Key))
        {
            foreach (var (functionPattern, enabled) in OrderBySpecificity(functions))
            {
                if (IsMatch(functionPattern, function.KernelFunction.Name))
                {
                    result = enabled;
                }
            }
        }

        return result;
    }

    private ValueEnumerable<OrderBy<Where<FromDictionary<string, ToolFunctionPatternRulesets>,
                    KeyValuePair<string, ToolFunctionPatternRulesets>>,
                KeyValuePair<string, ToolFunctionPatternRulesets>, string>,
            KeyValuePair<string, ToolFunctionPatternRulesets>>
        GetMatchingPluginRules(string pluginKey) =>
        this.AsValueEnumerable()
            .Where(pair => IsMatch(pair.Key, pluginKey))
            .OrderBy(static pair => GetSpecificity(pair.Key))
            .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    private static ValueEnumerable<OrderBy<FromDictionary<string, bool>,
                KeyValuePair<string, bool>, string>,
            KeyValuePair<string, bool>>
        OrderBySpecificity(ToolFunctionPatternRulesets rulesets) => rulesets
        .AsValueEnumerable()
        .OrderBy(static pair => GetSpecificity(pair.Key))
        .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    private ToolPatternRulesets CopyCore()
    {
        var result = new ToolPatternRulesets(Count);
        foreach (var (pluginPattern, functions) in this)
        {
            result.Add(pluginPattern, new ToolFunctionPatternRulesets(functions));
        }

        return result;
    }

    private static int GetSpecificity(string pattern) =>
        pattern.Sum(static character => character switch { '*' => 0, '?' => 1, _ => 2 });

    private static bool IsMatch(string pattern, string value) =>
        FileSystemName.MatchesSimpleExpression(pattern, value, ignoreCase: true);
}

public class ToolFunctionPatternRulesets : Dictionary<string, bool>
{
    public ToolFunctionPatternRulesets() : base(StringComparer.OrdinalIgnoreCase) { }

    public ToolFunctionPatternRulesets(int capacity) : base(capacity, StringComparer.OrdinalIgnoreCase) { }

    public ToolFunctionPatternRulesets(IDictionary<string, bool> dictionary) : base(dictionary, StringComparer.OrdinalIgnoreCase)
    {
    }
}

public static class ToolPatternRulesetsExtensions
{
    public static ToolPatternRulesets? TryUnion(this ToolPatternRulesets? source, ToolPatternRulesets? overrides = null)
    {
        if (source is null)
        {
            return overrides is null ?
                null :
                new ToolPatternRulesets(
                    overrides.ToDictionary(
                        static pair => pair.Key,
                        static pair => new ToolFunctionPatternRulesets(pair.Value),
                        StringComparer.OrdinalIgnoreCase));
        }

        return source.Union(overrides);
    }
}