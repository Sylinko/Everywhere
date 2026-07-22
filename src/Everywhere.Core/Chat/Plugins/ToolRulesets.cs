using System.IO.Enumeration;
using Everywhere.Serialization;
using MessagePack;
using ZLinq.Linq;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Provides effective enablement decisions for plugins and functions.
/// A missing decision means the caller should inherit from the preceding settings layer.
/// </summary>
public interface IToolRulesets
{
    bool? IsPluginAllowed(ChatPlugin plugin);

    bool? IsFunctionAllowed(ChatPlugin plugin, ChatFunction function);
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
[MessagePackFormatter(typeof(ToolRulesetsMessagePackFormatter))]
public class ToolRulesets : Dictionary<string, ToolFunctionRulesets>, IToolRulesets
{
    public ToolRulesets() : base(StringComparer.OrdinalIgnoreCase) { }

    public ToolRulesets(int capacity) : base(capacity, StringComparer.OrdinalIgnoreCase) { }

    public ToolRulesets(IDictionary<string, ToolFunctionRulesets> dictionary) : base(dictionary, StringComparer.OrdinalIgnoreCase)
    {
    }

    public ToolRulesets Union(ToolRulesets? overrides)
    {
        if (overrides is null) return CopyCore();

        var result = CopyCore();
        foreach (var (pluginPattern, functionOverrides) in overrides)
        {
            if (!result.TryGetValue(pluginPattern, out var functions))
            {
                result[pluginPattern] = new ToolFunctionRulesets(functionOverrides);
                continue;
            }

            foreach (var (functionPattern, value) in functionOverrides)
            {
                functions[functionPattern] = value;
            }
        }

        return result;
    }

    public bool? IsPluginAllowed(ChatPlugin plugin)
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

    public bool? IsFunctionAllowed(ChatPlugin plugin, ChatFunction function)
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

    private ValueEnumerable<OrderBy<Where<FromDictionary<string, ToolFunctionRulesets>,
                    KeyValuePair<string, ToolFunctionRulesets>>,
                KeyValuePair<string, ToolFunctionRulesets>, string>,
            KeyValuePair<string, ToolFunctionRulesets>>
        GetMatchingPluginRules(string pluginKey) =>
        this.AsValueEnumerable()
            .Where(pair => IsMatch(pair.Key, pluginKey))
            .OrderBy(static pair => GetSpecificity(pair.Key))
            .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    private static ValueEnumerable<OrderBy<FromDictionary<string, bool>,
                KeyValuePair<string, bool>, string>,
            KeyValuePair<string, bool>>
        OrderBySpecificity(ToolFunctionRulesets rulesets) => rulesets
        .AsValueEnumerable()
        .OrderBy(static pair => GetSpecificity(pair.Key))
        .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    private ToolRulesets CopyCore()
    {
        var result = new ToolRulesets(Count);
        foreach (var (pluginPattern, functions) in this)
        {
            result.Add(pluginPattern, new ToolFunctionRulesets(functions));
        }

        return result;
    }

    private static int GetSpecificity(string pattern) =>
        pattern.Sum(static character => character switch { '*' => 0, '?' => 1, _ => 2 });

    private static bool IsMatch(string pattern, string value) =>
        FileSystemName.MatchesSimpleExpression(pattern, value, ignoreCase: true);
}

public class ToolFunctionRulesets : Dictionary<string, bool>
{
    public ToolFunctionRulesets() : base(StringComparer.OrdinalIgnoreCase) { }

    public ToolFunctionRulesets(int capacity) : base(capacity, StringComparer.OrdinalIgnoreCase) { }

    public ToolFunctionRulesets(IDictionary<string, bool> dictionary) : base(dictionary, StringComparer.OrdinalIgnoreCase)
    {
    }
}

public static class ToolRulesetsExtensions
{
    public static ToolRulesets? TryUnion(this ToolRulesets? source, ToolRulesets? overrides = null)
    {
        if (source is null)
        {
            return overrides is null ?
                null :
                new ToolRulesets(
                    overrides.ToDictionary(
                        static pair => pair.Key,
                        static pair => new ToolFunctionRulesets(pair.Value),
                        StringComparer.OrdinalIgnoreCase));
        }

        return source.Union(overrides);
    }
}