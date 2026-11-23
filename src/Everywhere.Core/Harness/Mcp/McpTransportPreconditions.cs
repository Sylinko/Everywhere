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

        if (exception is ClientTransportClosedException closedException)
        {
            var isCommandNotFound = IsWindowsCommandNotFound(closedException);
            var targetName = isCommandNotFound
                ? NormalizeCommand(stdio.Command)
                : string.IsNullOrWhiteSpace(stdio.Name)
                    ? NormalizeCommand(stdio.Command)
                    : stdio.Name;
            handledException = new HandledException(
                closedException,
                new FormattedDynamicLocaleKey(
                    isCommandNotFound
                        ? LocaleKey.ChatPluginManager_McpPluginCommandNotFound_StartFailure
                        : LocaleKey.ChatPluginManager_Common_FailedToStartMcpPlugin,
                    new DirectLocaleKey(targetName)),
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

    /// <summary>
    /// OS specific heuristic to detect a Windows "command not found" error. This is a common failure mode when the user has not installed the required runtime for an MCP stdio plugin.
    /// </summary>
    /// <param name="exception"></param>
    /// <returns></returns>
    internal static bool IsWindowsCommandNotFound(ClientTransportClosedException exception)
    {
        return OperatingSystem.IsWindows() && exception.Details is StdioClientCompletionDetails { ExitCode: 9009 };
    }

    private static string NormalizeCommand(string command) => command.Trim().Trim('"');
}
