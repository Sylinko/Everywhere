using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using ModelContextProtocol.Client;

namespace Everywhere.Harness.Mcp;

/// <summary>
/// Validates runtime dependencies before establishing an MCP connection
/// and translates OS process launch failures into localized <see cref="HandledException"/> instances.
/// </summary>
internal static class McpTransportPreconditions
{
    public static RuntimeDependency? GetMissingRuntimeDependency(
        IRuntimeManager runtimeManager,
        McpTransportConfiguration? configuration
    )
    {
        return configuration is StdioMcpTransportConfiguration stdio
            ? runtimeManager.GetMissingDependency(stdio.Command)
            : null;
    }

    public static async Task EnsureReadyAsync(
        IRuntimeManager runtimeManager,
        McpTransportConfiguration configuration,
        Action? onRuntimeRefreshed,
        CancellationToken cancellationToken
    )
    {
        if (configuration.HasErrors)
        {
            throw new HandledException(
                new InvalidOperationException("MCP transport configuration is not valid."),
                new DynamicLocaleKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration));
        }

        if (configuration is not StdioMcpTransportConfiguration stdio)
        {
            return;
        }

        if (!runtimeManager.HasRefreshed)
        {
            await runtimeManager.RefreshAsync(cancellationToken);
        }

        onRuntimeRefreshed?.Invoke();

        if (runtimeManager.GetMissingDependency(stdio.Command) is { } missingDependency)
        {
            throw new HandledException(
                new FileNotFoundException(
                    $"MCP stdio command '{stdio.Command}' requires missing runtime '{missingDependency.DisplayName}'.",
                    stdio.Command),
                new FormattedDynamicLocaleKey(
                    LocaleKey.ChatPluginManager_McpPluginMissingRuntime_StartFailure,
                    new DirectLocaleKey(missingDependency.DisplayName)));
        }
    }

    public static bool TryCreateStartHandledException(
        McpTransportConfiguration transportConfiguration,
        Exception exception,
        [NotNullWhen(true)] out HandledException? handledException
    )
    {
        handledException = null;
        if (exception is HandledException handled)
        {
            handledException = handled;
            return true;
        }

        if (transportConfiguration is not StdioMcpTransportConfiguration stdio)
        {
            return false;
        }

        if (exception is ClientTransportClosedException)
        {
            var displayName = string.IsNullOrWhiteSpace(stdio.Name) ? NormalizeCommand(stdio.Command) : stdio.Name;
            handledException = new HandledException(
                exception,
                new FormattedDynamicLocaleKey(
                    LocaleKey.ChatPluginManager_Common_FailedToStartMcpPlugin,
                    new DirectLocaleKey(displayName)),
                showDetails: true);
            return true;
        }

        var systemException = exception.Segregate().FirstOrDefault(static e =>
            e is Win32Exception or FileNotFoundException or DirectoryNotFoundException);
        if (systemException is null)
        {
            return false;
        }

        var command = NormalizeCommand(stdio.Command);
        var messageKey = new FormattedDynamicLocaleKey(
            LocaleKey.ChatPluginManager_McpPluginCommandNotFound_StartFailure,
            new DirectLocaleKey(command));
        handledException = new HandledException(
            new InvalidOperationException(
                $"Failed to start MCP stdio command '{command}'. Error type: {systemException.GetType().Name}.",
                exception),
            messageKey);
        return true;
    }

    private static string NormalizeCommand(string command) => command.Trim().Trim('"');
}
