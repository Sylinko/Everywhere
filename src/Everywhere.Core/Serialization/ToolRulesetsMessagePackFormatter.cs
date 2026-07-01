using Everywhere.Chat.Plugins;
using MessagePack;
using MessagePack.Formatters;

namespace Everywhere.Serialization;

public sealed class ToolRulesetsMessagePackFormatter : DictionaryFormatterBase<string, bool, ToolRulesets>
{
    protected override ToolRulesets Create(int count, MessagePackSerializerOptions options)
    {
        return new ToolRulesets(count);
    }

    protected override void Add(ToolRulesets collection, int index, string key, bool value, MessagePackSerializerOptions options)
    {
        collection.Add(key, value);
    }
}