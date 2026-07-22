using Everywhere.Chat.Plugins;
using MessagePack;
using MessagePack.Formatters;

namespace Everywhere.Serialization;

/// <summary>
/// Serializes the nested tool ruleset representation and reads the legacy flat representation.
/// </summary>
public sealed class ToolRulesetsMessagePackFormatter : IMessagePackFormatter<ToolRulesets?>
{
    public void Serialize(ref MessagePackWriter writer, ToolRulesets? value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        writer.WriteMapHeader(value.Count);
        var formatter = options.Resolver.GetFormatterWithVerify<ToolFunctionRulesets>();
        foreach (var (key, functionRulesets) in value)
        {
            writer.Write(key);
            formatter.Serialize(ref writer, functionRulesets, options);
        }
    }

    public ToolRulesets? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            return null;
        }

        var count = reader.ReadMapHeader();
        var result = new ToolRulesets(count);
        var formatter = options.Resolver.GetFormatterWithVerify<ToolFunctionRulesets>();
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadString() ?? throw new MessagePackSerializationException("Tool ruleset key cannot be null.");
            switch (reader.NextMessagePackType)
            {
                case MessagePackType.Map:
                case MessagePackType.Nil:
                    result.Add(key, formatter.Deserialize(ref reader, options));
                    break;
                case MessagePackType.Boolean:
                    AddLegacyRule(result, key, reader.ReadBoolean());
                    break;
                default:
                    throw new MessagePackSerializationException(
                        $"Unsupported tool ruleset value type '{reader.NextMessagePackType}'.");
            }
        }

        return result;
    }

    private static void AddLegacyRule(ToolRulesets rulesets, string legacyKey, bool enabled)
    {
        var firstDot = legacyKey.IndexOf('.');
        var secondDot = firstDot < 0 ? -1 : legacyKey.IndexOf('.', firstDot + 1);
        if (firstDot <= 0 || secondDot <= firstDot + 1 || secondDot == legacyKey.Length - 1)
        {
            throw new MessagePackSerializationException(
                $"Legacy tool ruleset key '{legacyKey}' must contain a plugin and function component.");
        }

        var pluginKey = legacyKey[..secondDot];
        var functionPattern = legacyKey[(secondDot + 1)..];
        if (!rulesets.TryGetValue(pluginKey, out var functionRulesets))
        {
            functionRulesets = new ToolFunctionRulesets();
            rulesets.Add(pluginKey, functionRulesets);
        }

        functionRulesets[functionPattern] = enabled;
    }
}