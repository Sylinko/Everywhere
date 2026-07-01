using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Everywhere.Chat.Plugins.Mcp;

/// <summary>
/// An <see cref="AIFunction"/> wrapper around <see cref="McpClientTool"/> that intercepts invocations,
/// detects session expiry errors, and silently reconnects and retries.
/// </summary>
internal sealed class ManagedMcpClientTool : AIFunction
{
    public override string Name { get; }

    public override string Description => ProtocolTool.Description ?? string.Empty;

    public override JsonElement JsonSchema => ProtocolTool.InputSchema;

    public override JsonElement? ReturnJsonSchema => ProtocolTool.OutputSchema;

    internal Tool ProtocolTool { get; }

    private readonly ManagedMcpClient _managedClient;

    internal ManagedMcpClientTool(Tool protocolTool, ManagedMcpClient managedClient, string escapedName)
    {
        ProtocolTool = protocolTool;
        _managedClient = managedClient;
        Name = escapedName;
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        _managedClient.CallToolAsync(ProtocolTool.Name, arguments, cancellationToken); // Use original tool name, not escaped name
}
