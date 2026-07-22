using System.Diagnostics.CodeAnalysis;
using Everywhere.AI;
using Everywhere.Collections;
using Everywhere.Common;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Manages chat plugins, both built-in and MCP plugins.
/// </summary>
public interface IChatPluginManager
{
    /// <summary>
    /// Gets the list of built-in chat plugins for Binding use in the UI.
    /// </summary>
    IReadOnlyBindableList<BuiltInChatPlugin> BuiltInPlugins { get; }

    /// <summary>
    /// Gets the list of MCP chat plugins for Binding use in the UI.
    /// </summary>
    IReadOnlyBindableList<McpChatPlugin> McpPlugins { get; }

    /// <summary>
    /// Creates a new MCP plugin based on the provided configuration.
    /// </summary>
    /// <param name="configuration"></param>
    McpChatPlugin CreateMcpPlugin(McpTransportConfiguration configuration);

    /// <summary>
    /// Updates an existing MCP plugin with a new configuration.
    /// </summary>
    /// <param name="mcpChatPlugin"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task UpdateMcpPluginAsync(McpChatPlugin mcpChatPlugin, McpTransportConfiguration configuration);

    /// <summary>
    /// Creates a new MCP client based on the provided configuration. If it's a local client, it will start the local server process.
    /// </summary>
    /// <param name="mcpChatPlugin"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StartMcpClientAsync(McpChatPlugin mcpChatPlugin, CancellationToken cancellationToken);

    /// <summary>
    /// Stops and disposes the MCP client. If it's a local client, it will stop the local server process.
    /// </summary>
    /// <param name="mcpChatPlugin"></param>
    /// <returns></returns>
    Task StopMcpClientAsync(McpChatPlugin mcpChatPlugin);

    /// <summary>
    /// Stops and removes the MCP plugin.
    /// </summary>
    /// <param name="mcpChatPlugin"></param>
    Task RemoveMcpPluginAsync(McpChatPlugin mcpChatPlugin);

    /// <summary>
    /// Gets the missing runtime dependency for the MCP plugin, if any.
    /// </summary>
    RuntimeDependency? GetMissingRuntimeDependency(McpChatPlugin mcpChatPlugin);

    /// <summary>
    /// Refreshes runtime warning messages for MCP plugins.
    /// </summary>
    void RefreshMcpRuntimeWarnings();

    /// <summary>
    /// Creates a new scope for available chat plugins and their functions.
    /// This method should be lightweight and fast, as it is called frequently.
    /// Functions in the scope must not have the same name.
    /// </summary>
    /// <returns></returns>
    Task<IChatPluginScope> CreateScopeAsync(
        Assistant assistant,
        ChatContext chatContext,
        IToolRulesets? toolRulesets,
        CancellationToken cancellationToken);
}

/// <summary>
/// A scope for chat plugins, snapshot and can be used to track state during a chat session.
/// </summary>
public interface IChatPluginScope
{
    /// <summary>
    /// Gets all plugins in this scope.
    /// </summary>
    IReadOnlyList<ChatPlugin> Plugins { get; }

    /// <summary>
    /// Tries to get the plugin and function by function name. Returns similar function names if not found.
    /// </summary>
    /// <param name="functionName"></param>
    /// <param name="plugin"></param>
    /// <param name="function"></param>
    /// <param name="similarFunctionNames"></param>
    /// <returns></returns>
    bool TryGetPluginAndFunction(
        string functionName,
        [NotNullWhen(true)] out ChatPlugin? plugin,
        [NotNullWhen(true)] out ChatFunction? function,
        [NotNullWhen(false)] out IReadOnlyList<string>? similarFunctionNames);
}
