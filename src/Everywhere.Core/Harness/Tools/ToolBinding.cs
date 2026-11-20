using System.Buffers;
using Everywhere.Chat.Plugins;
using Everywhere.Utilities;
using Microsoft.Extensions.AI;

namespace Everywhere.Harness;

/// <summary>
/// Binds a generation's deduplicated model-facing <see cref="AIFunctionDeclaration"/>
/// to the underlying <see cref="AIFunction"/> invoked after host approval.
/// </summary>
public sealed class ToolBinding
{
    /// <summary>
    /// Maximum model-facing tool declaration name length enforced by major providers (OpenAI / Gemini).
    /// </summary>
    public const int MaxWireNameLength = 64;

    /// <summary>
    /// Gets the plugin snapshot that owns this tool.
    /// </summary>
    public ChatPluginSnapshot Plugin { get; }

    /// <summary>
    /// Gets the snapshot of the host function descriptor.
    /// </summary>
    public ChatFunction Function { get; }

    /// <summary>
    /// Gets the declaration exposed to the model (incapable of self-execution).
    /// </summary>
    public AIFunctionDeclaration Declaration { get; }

    private static readonly SearchValues<char> AllowedWireNameChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-");

    private readonly AIFunction _target;

    /// <summary>
    /// Reserves one unique name from the generation's shared name set and captures its invocation target.
    /// </summary>
    public ToolBinding(ChatPluginSnapshot plugin, ChatFunction function, NumberedDeduplicator names)
    {
        Plugin = plugin;
        // MCP discovery can refresh the UI descriptor; freeze it before reading either declaration or target.
        Function = function is McpChatFunction mcp ? mcp.Snapshot() : function;
        _target = Function.Function;
        var wireName = plugin.FormatWireName(SanitizeWireName(_target.Name));
        var name = names.Deduplicate(wireName, MaxWireNameLength);
        Declaration = AIFunctionFactory.CreateDeclaration(
            name,
            _target.Description,
            _target.JsonSchema,
            _target.ReturnJsonSchema);
    }

    private static string SanitizeWireName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return "tool";
        if (!rawName.AsSpan().ContainsAnyExcept(AllowedWireNameChars)) return rawName;

        return string.Create(
            rawName.Length,
            rawName,
            static (destination, source) =>
            {
                for (var i = 0; i < source.Length; i++)
                {
                    var ch = source[i];
                    destination[i] = AllowedWireNameChars.Contains(ch) ? ch : '_';
                }
            });
    }

    /// <summary>
    /// Executes this tool after host approval, preserving the provider's call identity.
    /// </summary>
    public async Task<FunctionResultContent> InvokeAsync(
        IServiceProvider services,
        FunctionCallContent call,
        CancellationToken cancellationToken
    )
    {
        if (call.Exception is { } error)
        {
            throw error;
        }

        var arguments = new AIFunctionArguments(call.Arguments) { Services = services };
        var result = await _target.InvokeAsync(arguments, cancellationToken);
        return new FunctionResultContent(call.CallId, result);
    }
}
