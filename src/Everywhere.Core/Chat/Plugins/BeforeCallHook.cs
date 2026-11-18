using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Creates optional display content before approval and tool execution, including calls without arguments.
/// </summary>
/// <remarks>
/// Runs synchronously on the calling thread, which is not necessarily the UI thread.
/// Approval may subsequently deny the call; this hook must not perform the tool's operation or grant consent.
/// Returned content can outlive the plugin and should use host-owned display types and values.
/// </remarks>
/// <param name="content">The original call, including its identity and possibly absent arguments.</param>
/// <returns>A display block used for consent presentation and fallback display, or null to add no block.</returns>
public delegate ChatPluginDisplayBlock? BeforeCallHook(FunctionCallContent content);

/// <summary>
/// Helper methods for extracting typed values from <see cref="IDictionary{TKey,TValue}"/> tool arguments.
/// </summary>
public static class FunctionCallArgumentsExtensions
{
    /// <summary>
    /// Extracts a string argument whether stored as a CLR <see cref="string"/> or a <see cref="JsonElement"/>.
    /// </summary>
    public static bool TryGetString(
        this IDictionary<string, object?> arguments,
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
