using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ZLinq;

namespace Everywhere.Chat.Plugins.Mcp;

/// <summary>
/// Manages the lifecycle of an <see cref="McpClient"/>, including creation, reconnection on session expiry, and disposal.
/// Encapsulates transport creation logic (Stdio / HTTP) and watchdog registration.
/// </summary>
public sealed partial class ManagedMcpClient : IAsyncDisposable
{
    public McpChatPlugin McpChatPlugin { get; }

    private readonly ChatPluginManager _manager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWatchdogManager _watchdogManager;
    private readonly IKeyValueStorage _keyValueStorage;
    private readonly IRuntimeManager _runtimeManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly McpTransportConfiguration _transportConfiguration;

    private volatile bool _isDisposed;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly ReusableCancellationTokenSource _connectionCts = new();

    private McpClient? _mcpClient;
    private IClientTransport? _clientTransport;
    private Process? _mcpProcess;
    private bool _isSessionExpired;

    /// <summary>
    /// Manages the lifecycle of an <see cref="McpClient"/>, including creation, reconnection on session expiry, and disposal.
    /// Encapsulates transport creation logic (Stdio / HTTP) and watchdog registration.
    /// </summary>
    public ManagedMcpClient(McpChatPlugin mcpChatPlugin, ChatPluginManager manager, IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
    {
        McpChatPlugin = mcpChatPlugin;
        _manager = manager;
        _httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        _watchdogManager = serviceProvider.GetRequiredService<IWatchdogManager>();
        _keyValueStorage = serviceProvider.GetRequiredService<IKeyValueStorage>();
        _runtimeManager = serviceProvider.GetRequiredService<IRuntimeManager>();
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ManagedMcpClient>();
        _transportConfiguration = mcpChatPlugin.TransportConfiguration ??
            throw new InvalidOperationException("MCP plugin must have a transport configuration.");

        McpChatPlugin.EditFunctions(list => list.Reset(LoadCachedTools().OrderBy(x => x.ProtocolTool.Name).Select(x => new McpChatFunction(x))));
    }

    /// <summary>
    /// Starts the MCP client by creating a transport and connecting.
    /// Sets up process watchdog for stdio transports.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);
    }

    private async Task<McpClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        McpClient? mcpClient;
        if ((mcpClient = _mcpClient) is not null && !_isSessionExpired)
        {
            return mcpClient;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if ((mcpClient = _mcpClient) is not null && !_isSessionExpired)
            {
                return mcpClient;
            }

            if (_mcpProcess is not null)
            {
                _mcpProcess.Exited -= HandleMcpProcessExited;
                _mcpProcess = null;
            }

            if (_mcpClient is not null)
            {
                await _mcpClient.DisposeAsync();
                _mcpClient = null;
            }

            _connectionCts.Cancel();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _connectionCts.Token);

            _clientTransport = CreateTransport();
            mcpClient = await McpClient.CreateAsync(
                _clientTransport,
                null,
                _loggerFactory,
                linkedCts.Token);

            _mcpClient = mcpClient;
            McpChatPlugin.IsRunning = true;
            _isSessionExpired = false;

            await RegisterStdioWatchdogAsync(_clientTransport);

            // if (mcpClient.ServerInfo.Icons is { Count: > 0 } icons)
            // {
            //     McpChatPlugin.UpdateBeautifulIcon(icons[0].Source);
            // }

            var tools = (await ListToolsAsync(linkedCts.Token)).OrderBy(x => x.ProtocolTool.Name).ToArray();
            McpChatPlugin.EditFunctions(list =>
            {
                int i = 0, j = 0;
                while (i < list.Count && j < tools.Length)
                {
                    var compare = string.Compare(list[i].OriginalName, tools[j].ProtocolTool.Name, StringComparison.Ordinal);
                    switch (compare)
                    {
                        case 0:
                            list[i].Update(tools[j]);
                            i++;
                            j++;
                            break;
                        case < 0:
                            list.RemoveAt(i);
                            break;
                        default:
                            list.Insert(i, new McpChatFunction(tools[j]));
                            i++;
                            j++;
                            break;
                    }
                }

                while (i < list.Count)
                {
                    list.RemoveAt(i);
                }

                while (j < tools.Length)
                {
                    list.Add(new McpChatFunction(tools[j]));
                    j++;
                }
            });
        }
        finally
        {
            _connectionLock.Release();
        }

        return mcpClient;
    }

    private void HandleMcpProcessExited(object? sender, EventArgs e)
    {
        if (sender is Process process)
        {
            process.Exited -= HandleMcpProcessExited;
        }

        McpChatPlugin.IsRunning = false;
        _mcpProcess = null;
        _isSessionExpired = true;
    }

    /// <summary>
    /// Lists tools from the MCP client, wrapping them in <see cref="ManagedMcpClientTool"/> with escaped names.
    /// </summary>
    private async Task<IList<ManagedMcpClientTool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var result = await ListToolsCoreAsync(cancellationToken);
        SaveCachedTools(result);
        return result;
    }

    /// <summary>
    /// Saves the listed tools into KeyValueStorage
    /// </summary>
    /// <param name="tools"></param>
    private void SaveCachedTools(IList<ManagedMcpClientTool> tools)
    {
        var json = JsonSerializer.Serialize(
            tools.Select(t => new SerializableTool(t.Name, t.ProtocolTool)),
            SerializableToolJsonSerializerContext.Default.IEnumerableSerializableTool);
        _keyValueStorage.Set($"ManagedMcpClientTools:{McpChatPlugin.Id}", json);
    }

    private IEnumerable<ManagedMcpClientTool> LoadCachedTools()
    {
        var json = _keyValueStorage.Get<string>($"ManagedMcpClientTools:{McpChatPlugin.Id}");
        if (json is null) return [];

        try
        {
            var tools = JsonSerializer.Deserialize(json, SerializableToolJsonSerializerContext.Default.IEnumerableSerializableTool);
            return tools?.Select(t => new ManagedMcpClientTool(t.Tool, this, t.Name)) ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize tools for plugin {PluginName}.", McpChatPlugin.Name);
            return [];
        }
    }

    public async ValueTask<object?> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var client = await EnsureConnectedAsync(cancellationToken);

        try
        {
            return await CallToolCoreAsync(client, toolName, arguments, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsSessionExpired())
        {
            _logger.LogWarning(ex, "Session expired during Calling {ToolName}, reconnecting...", McpChatPlugin.Name);

            _isSessionExpired = true;
            client = await EnsureConnectedAsync(cancellationToken);
            return await CallToolCoreAsync(client, toolName, arguments, cancellationToken: cancellationToken);
        }
    }

    private async static ValueTask<object?> CallToolCoreAsync(
        McpClient client,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var result = await client.CallToolAsync(toolName, arguments, null, null, cancellationToken).ConfigureAwait(false);

        // We want to translate the result content into AIContent, using AIContent as the exchange types, so
        // that downstream IChatClients can specialize handling based on the content (e.g. sending image content
        // back to the AI service as a multi-modal tool response). However, when there is additional information
        // carried by the CallToolResult outside of its ContentBlocks, just returning AIContent from those ContentBlocks
        // would lose that information. So, we only do the translation if there is no additional information to preserve.
        if (result.IsError is not true &&
            result.StructuredContent is null &&
            result.Meta is not { Count: > 0 })
        {
            switch (result.Content.Count)
            {
                case 1 when result.Content[0].ToAIContent() is { } aiContent:
                    return aiContent;
                case > 1 when result.Content.Select(c => c.ToAIContent()).ToArray() is { } aiContents && aiContents.All(static c => c is not null):
                    return aiContents;
            }
        }

        return JsonSerializer.SerializeToElement(result, CallToolResultJsonSerializerContext.Default.CallToolResult);
    }

    /// <summary>
    /// Determines whether the given exception indicates an MCP session expired error.
    /// </summary>
    private bool IsSessionExpired()
    {
        // Only attempt reconnection for HTTP transports.
        if (_transportConfiguration is not HttpMcpTransportConfiguration)
            return false;

        if (CheckCompletionForSessionExpiry())
            return true;

        return false;
    }

    /// <summary>
    /// Synchronously checks the <see cref="McpClient.Completion"/> task to determine
    /// if the session has expired (HTTP 404 from the MCP server).
    /// </summary>
    private bool CheckCompletionForSessionExpiry()
    {
        if (_isSessionExpired)
            return true;

        if (_mcpClient?.Completion is
            {
                IsCompletedSuccessfully: true,
                Result: HttpClientCompletionDetails { HttpStatusCode: HttpStatusCode.NotFound }
            })
        {
            _isSessionExpired = true;
            return true;
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        McpChatPlugin.IsRunning = false;
        _manager.HandleClientDisposed(this);
        _connectionCts.Cancel();

        await _connectionLock.WaitAsync();
        try
        {
            if (_mcpProcess is not null)
            {
                _mcpProcess.Exited -= HandleMcpProcessExited;
                _mcpProcess = null;
            }

            if (_mcpClient is not null)
            {
                await _mcpClient.DisposeAsync();
                _mcpClient = null;
            }
        }
        finally
        {
            _connectionLock.Release();
            _connectionLock.Dispose();
        }
    }

    private async Task<IList<ManagedMcpClientTool>> ListToolsCoreAsync(CancellationToken cancellationToken)
    {
        if (_mcpClient is null) throw new InvalidOperationException("MCP client is not started.");

        var rawTools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);

        // Escape names and deduplicate.
        var nameCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var managedTools = new List<ManagedMcpClientTool>(rawTools.Count);

        foreach (var tool in rawTools)
        {
            var escapedName = EscapeToolName(tool.Name);

            if (nameCount.TryGetValue(escapedName, out var count))
            {
                nameCount[escapedName] = count + 1;
                escapedName = $"{escapedName}_{count}";
            }
            else
            {
                nameCount[escapedName] = 1;
            }

            managedTools.Add(new ManagedMcpClientTool(tool.ProtocolTool, this, escapedName));
        }

        return managedTools;
    }

    /// <summary>
    /// MCP tool names allow A-Z a-z 0-9 _ - .
    /// but SK/AIFunction only allows A-Z a-z 0-9 _
    /// so we replace - and . with _
    /// </summary>
    private static string EscapeToolName(string name)
    {
        return name.AsSpan().IndexOfAny('-', '.') >= 0 ?
            new string(name.AsValueEnumerable().Select(static c => c is '-' or '.' ? '_' : c).ToArray()) :
            name;
    }

    private IClientTransport CreateTransport()
    {
        return _transportConfiguration switch
        {
            StdioMcpTransportConfiguration stdio => new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = stdio.Name,
                    Command = stdio.Command,
                    Arguments = stdio.Arguments
                        .AsValueEnumerable()
                        .Select(x => x.Value)
                        .Where(x => !x.IsNullOrWhiteSpace())
                        .ToArray(),
                    WorkingDirectory = EnsureWorkingDirectory(stdio.WorkingDirectory),
                    EnvironmentVariables = EnsureLatestPath(
                        stdio.EnvironmentVariables
                            .AsValueEnumerable()
                            .Where(kv => !kv.Key.IsNullOrWhiteSpace())
                            .DistinctBy(
                                kv => kv.Key,
                                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                            .ToDictionary(
                                kv => kv.Key,
                                kv => kv.Value,
                                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal),
                        _runtimeManager.GetPathEntries()),
                },
                _loggerFactory),
            HttpMcpTransportConfiguration sse => new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = sse.Name,
                    Endpoint = new Uri(sse.Endpoint, UriKind.Absolute),
                    AdditionalHeaders = sse.Headers
                        .AsValueEnumerable()
                        .Where(kv => !kv.Key.IsNullOrWhiteSpace() && !kv.Value.IsNullOrWhiteSpace())
                        .DistinctBy(kv => kv.Key)
                        .ToDictionary(kv => kv.Key, kv => kv.Value),
                    TransportMode = sse.TransportMode
                },
                _httpClientFactory.CreateClient(McpServiceExtensions.McpClientName),
                _loggerFactory),
            _ => throw new InvalidOperationException("Unsupported MCP transport configuration type.")
        };
    }

    private async Task RegisterStdioWatchdogAsync(IClientTransport clientTransport)
    {
        if (_mcpClient is null || clientTransport is not StdioClientTransport) return;

        var processId = -1;
        try
        {
            var transport = GetMcpClientTransport(_mcpClient);
            if (GetStdioClientSessionTransportProcess(transport) is { HasExited: false, Id: > 0 } process)
            {
                _mcpProcess = process;
                process.Exited += HandleMcpProcessExited;

                await _watchdogManager.RegisterProcessAsync(process.Id);
                processId = process.Id;
            }
        }
        finally
        {
            if (processId == -1 && _transportConfiguration is StdioMcpTransportConfiguration stdio)
            {
                _logger.LogWarning(
                    "MCP started with stdio transport, but failed to get the underlying process ID for watchdog registration. " +
                    "Command: {Command}, Arguments: {Arguments}",
                    stdio.Command,
                    stdio.Arguments);
            }
        }
    }

    private string EnsureWorkingDirectory(string? workingDirectory)
    {
        return Directory.Exists(workingDirectory) ?
            workingDirectory :
            RuntimeConstants.EnsureWritableDataFolderPath("plugins", "mcp", McpChatPlugin.Id.ToString("N"));
    }

    private static Dictionary<string, string?> EnsureLatestPath(
        Dictionary<string, string?> environmentVariables,
        IReadOnlyList<string> managedPathEntries)
    {
        var latestPath = EnvironmentVariableUtilities.GetLatestPathVariable();

        var pathBuilder = new StringBuilder();
        var seenPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        void AppendPaths(string? paths)
        {
            if (paths.IsNullOrEmpty()) return;

            foreach (var path in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!seenPaths.Add(path)) continue;
                if (pathBuilder.Length > 0) pathBuilder.Append(Path.PathSeparator);
                pathBuilder.Append(path);
            }
        }

        AppendPaths(string.Join(Path.PathSeparator, managedPathEntries));
        AppendPaths(latestPath);

        if (environmentVariables.TryGetValue("PATH", out var existingPath) && !existingPath.IsNullOrEmpty())
        {
            AppendPaths(existingPath);
        }

        if (pathBuilder.Length == 0) return environmentVariables;

        environmentVariables["PATH"] = pathBuilder.ToString();
        return environmentVariables;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_transport")]
    private static extern ref ITransport GetMcpClientTransport(
        [UnsafeAccessorType("ModelContextProtocol.Client.McpClientImpl, ModelContextProtocol.Core")]
        object client);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_process")]
    private static extern ref Process GetStdioClientSessionTransportProcess(
        [UnsafeAccessorType("ModelContextProtocol.Client.StdioClientSessionTransport, ModelContextProtocol.Core")]
        object transport);

    /// <summary>
    /// Escaped name & tool
    /// </summary>
    /// <param name="Name"></param>
    /// <param name="Tool"></param>
    private readonly record struct SerializableTool(string Name, Tool Tool);

    [JsonSerializable(typeof(IEnumerable<SerializableTool>))]
    private sealed partial class SerializableToolJsonSerializerContext : JsonSerializerContext;

    [JsonSerializable(typeof(CallToolResult))]
    private sealed partial class CallToolResultJsonSerializerContext : JsonSerializerContext;
}
