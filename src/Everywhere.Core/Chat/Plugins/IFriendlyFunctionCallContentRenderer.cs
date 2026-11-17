using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Renders tool call arguments into a user-friendly display block for UI presentation.
/// </summary>
public interface IFriendlyFunctionCallContentRenderer
{
    /// <summary>
    /// Renders the tool call arguments to a user-friendly format for UI display.
    /// </summary>
    ChatPluginDisplayBlock? Render(IReadOnlyDictionary<string, object?> arguments);
}

[AttributeUsage(AttributeTargets.Method)]
public class FriendlyFunctionCallContentRendererAttribute(Type rendererType) : Attribute
{
    public Type RendererType { get; } = rendererType;
}

/// <summary>
/// Helper methods for extracting typed values from <see cref="IReadOnlyDictionary{TKey,TValue}"/> tool arguments.
/// </summary>
public static class FunctionCallArgumentsExtensions
{
    /// <summary>
    /// Extracts a string argument whether stored as a CLR <see cref="string"/> or a <see cref="JsonElement"/>.
    /// </summary>
    public static bool TryGetString(
        this IReadOnlyDictionary<string, object?> arguments,
        string key,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!arguments.TryGetValue(key, out var raw)) return false;

        value = raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null
        };
        return value is not null;
    }
}
