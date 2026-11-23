using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Watchdog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Everywhere.Harness.Mcp;

/// <summary>
/// Manages the connection lifecycle of a single <see cref="McpChatPlugin"/> configuration generation,
/// including precondition validation, activity-aware startup timeouts, atomic tool discovery,
/// watchdog registration, reconnection, and disposal.
/// </summary>
public sealed partial class ManagedMcpClient : IAsyncDisposable
{
    private const int StatusThrottleMilliseconds = 100;
    private const int MaxStatusLineLength = 160;
    private const string July2026ProtocolVersion = "2026-07-28";

    public McpChatPlugin McpChatPlugin { get; }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWatchdogManager _watchdogManager;
    private readonly IKeyValueStorage _keyValueStorage;
    private readonly IRuntimeManager _runtimeManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly McpTransportConfiguration _transportConfiguration;
    private readonly Action? _onRuntimeRefreshed;

    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private readonly TimeSpan _httpStartupTimeout = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _stdioInactivityTimeout = TimeSpan.FromSeconds(20);
    private readonly TimeSpan _stdioMaxStartupTimeout = TimeSpan.FromMinutes(3);
    private readonly TimeSpan _stdioDiscoverProbeTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _stdioMaxDiscoverProbeWindow = TimeSpan.FromSeconds(30);

    private volatile Action<string>? _activeStartupStderrHandler;
    private volatile string? _preferredProtocolVersion;
    private int _disposeState;
    private McpClient? _mcpClient;
    private Process? _mcpProcess;
    private WatchdogRegistration? _watchdogRegistration;

    private bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    /// <summary>
    /// Initializes a new <see cref="ManagedMcpClient"/> bound to the plugin's current transport configuration
    /// and restores any cached tools into the plugin for offline configuration.
    /// </summary>
    public ManagedMcpClient(
        McpChatPlugin mcpChatPlugin,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory,
        Action? onRuntimeRefreshed = null
    )
    {
        McpChatPlugin = mcpChatPlugin;
        _httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        _watchdogManager = serviceProvider.GetRequiredService<IWatchdogManager>();
        _keyValueStorage = serviceProvider.GetRequiredService<IKeyValueStorage>();
        _runtimeManager = serviceProvider.GetRequiredService<IRuntimeManager>();
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ManagedMcpClient>();
        _transportConfiguration = mcpChatPlugin.TransportConfiguration ??
            throw new InvalidOperationException("MCP plugin must have a transport configuration.");
        _onRuntimeRefreshed = onRuntimeRefreshed;

        var cachedTools = LoadCachedTools().OrderBy(static x => x.ProtocolTool.Name, StringComparer.Ordinal).ToArray();
        if (cachedTools.Length > 0)
        {
            McpChatPlugin.EditFunctions(list => list.Reset(cachedTools.Select(static x => new McpChatFunction(x))));
        }
    }

    /// <summary>
    /// Starts the MCP client if it is not already healthy and connected.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken) =>
        StartAsync(onConnecting: null, progress: null, cancellationToken);

    internal async Task StartAsync(
        Func<ValueTask>? onConnecting,
        IProgress<IDynamicLocaleKey>? progress,
        CancellationToken cancellationToken
    )
    {
        await EnsureConnectedAsync(failedClient: null, onConnecting, progress, cancellationToken);
    }

    /// <summary>
    /// Cancels any in-flight connection attempt so shutdown or reconfiguration does not wait on a hung handshake.
    /// </summary>
    internal void RequestShutdown()
    {
        if (IsDisposed) return;

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal static string GetToolCacheKey(McpChatPlugin plugin) => $"ManagedMcpClientTools:{plugin.Id}";

    internal static void ClearCachedTools(IKeyValueStorage keyValueStorage, McpChatPlugin plugin)
    {
        keyValueStorage.Remove(GetToolCacheKey(plugin));
        plugin.EditFunctions(static list => list.Clear());
    }

    private async Task<McpClient> EnsureConnectedAsync(
        McpClient? failedClient,
        Func<ValueTask>? onConnecting,
        IProgress<IDynamicLocaleKey>? progress,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (_mcpClient is { } ready && !ReferenceEquals(ready, failedClient) && IsClientHealthy(ready, _mcpProcess))
            {
                return ready;
            }

            await McpTransportPreconditions.EnsureReadyAsync(_runtimeManager, _transportConfiguration, _onRuntimeRefreshed, cancellationToken);

            if (onConnecting is not null)
            {
                await onConnecting();
            }

            cancellationToken.ThrowIfCancellationRequested();

            await CloseConnectionAsync(_mcpClient);

            var isStdio = _transportConfiguration is StdioMcpTransportConfiguration;
            using var inactivityCts =
                isStdio ? new CancellationTokenSource(_stdioInactivityTimeout) : new CancellationTokenSource(_httpStartupTimeout);
            using var maxCts = isStdio ? new CancellationTokenSource(_stdioMaxStartupTimeout) : null;
            using var linkedCts = maxCts is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, inactivityCts.Token, maxCts.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, inactivityCts.Token);
            var token = linkedCts.Token;

            var stderrLock = new Lock();
            var startupCompleted = false;
            var lastStatusTick = 0L;
            var probeTimeProvider = isStdio && _preferredProtocolVersion is null
                ? new StdioDiscoverProbeTimeProvider(_stdioDiscoverProbeTimeout, _stdioMaxDiscoverProbeWindow)
                : null;

            _activeStartupStderrHandler = line =>
            {
                var trimmed = FormatStatusLine(line);
                if (trimmed.Length == 0) return;

                using (stderrLock.EnterScope())
                {
                    // ReSharper disable once AccessToModifiedClosure
                    if (startupCompleted) return;
                    // ReSharper disable once AccessToDisposedClosure
                    inactivityCts.CancelAfter(_stdioInactivityTimeout);
                    probeTimeProvider?.NotifyActivity();
                }

                if (progress is null) return;

                var now = Environment.TickCount64;
                if (Volatile.Read(ref lastStatusTick) != 0 && now - Volatile.Read(ref lastStatusTick) < StatusThrottleMilliseconds)
                {
                    return;
                }

                Volatile.Write(ref lastStatusTick, now);
                progress.Report(new DirectLocaleKey(trimmed));
            };

            try
            {
                McpClient candidate;
                IClientTransport transport;
                try
                {
                    (candidate, transport) = await CreateMcpClientWithProtocolWorkaroundAsync(
                        isStdio,
                        inactivityCts,
                        probeTimeProvider,
                        token);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                {
                    throw CreateStartupTimeoutException(isStdio, maxCts?.IsCancellationRequested == true, ex);
                }
                catch (TimeoutException ex)
                {
                    throw CreateStartupTimeoutException(isStdio, isMaxLimit: false, ex);
                }
                catch (Exception ex) when
                    (!token.IsCancellationRequested &&
                     McpTransportPreconditions.TryCreateStartHandledException(_transportConfiguration, ex, out var handledException))
                {
                    throw handledException;
                }

                try
                {
                    await RegisterStdioWatchdogAsync(candidate, transport);

                    var tools = await ListToolsAsync(candidate, token);
                    token.ThrowIfCancellationRequested();

                    SaveCachedTools(tools);
                    UpdateFunctions([.. tools.OrderBy(static x => x.ProtocolTool.Name, StringComparer.Ordinal)]);

                    McpChatPlugin.IsRunning = true;
                    _mcpClient = candidate;
                    return candidate;
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                {
                    await CloseConnectionAsync(candidate);
                    throw CreateStartupTimeoutException(isStdio, maxCts?.IsCancellationRequested == true, ex);
                }
                catch
                {
                    await CloseConnectionAsync(candidate);
                    throw;
                }
            }
            finally
            {
                using (stderrLock.EnterScope())
                {
                    startupCompleted = true;
                    _activeStartupStderrHandler = null;
                }
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task<(McpClient Client, IClientTransport Transport)> CreateMcpClientWithProtocolWorkaroundAsync(
        bool isStdio,
        CancellationTokenSource inactivityCts,
        StdioDiscoverProbeTimeProvider? probeTimeProvider,
        CancellationToken cancellationToken
    )
    {
        var protocolVersion = _preferredProtocolVersion;

        for (var attempt = 0;; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var transport = CreateTransport();
            var clientOptions = CreateMcpClientOptions(isStdio, protocolVersion, probeTimeProvider);

            try
            {
                var client = await McpClient.CreateAsync(transport, clientOptions, _loggerFactory, cancellationToken);
                if (protocolVersion is not null)
                {
                    _preferredProtocolVersion = protocolVersion;
                }

                return (client, transport);
            }
            catch (Exception ex) when
                (attempt == 0 &&
                 !cancellationToken.IsCancellationRequested &&
                 ShouldRetryWithJuly2026Protocol(ex))
            {
                if (protocolVersion is null)
                {
                    // TODO: WORKAROUND (ModelContextProtocol 2.4.0, upstream issue #1954-style race):
                    // If a stdio/HTTP server starts slower than DiscoverProbeTimeout (especially without emitting stderr),
                    // the SDK falls back to sending `initialize` on the same connection that already queued `server/discover`.
                    // Servers that lock onto 2026-07-28 upon seeing `server/discover` then reject `initialize` with -32022.
                    // Pin "2026-07-28" and disable DiscoverProbeTimeout for one immediate retry; commit the pin only after retry succeeds.
                    // Re-check whether this workaround can be removed when upgrading ModelContextProtocol.
                    protocolVersion = July2026ProtocolVersion;
                    if (isStdio)
                    {
                        inactivityCts.CancelAfter(_stdioInactivityTimeout);
                    }

                    _logger.LogInformation(
                        ex,
                        "MCP server '{Name}' rejected fallback initialize handshake with UnsupportedProtocolVersion (-32022); retrying with protocol version {ProtocolVersion}.",
                        _transportConfiguration.Name,
                        protocolVersion);
                }
                else
                {
                    var rejectedProtocolVersion = protocolVersion;
                    _preferredProtocolVersion = null;
                    protocolVersion = null;
                    if (isStdio)
                    {
                        inactivityCts.CancelAfter(_stdioInactivityTimeout);
                    }

                    _logger.LogInformation(
                        ex,
                        "MCP server '{Name}' rejected pinned protocol version {RejectedProtocolVersion} with UnsupportedProtocolVersion (-32022); retrying automatic protocol negotiation.",
                        _transportConfiguration.Name,
                        rejectedProtocolVersion);
                }
            }
        }
    }

    private McpClientOptions CreateMcpClientOptions(
        bool isStdio,
        string? protocolVersion,
        StdioDiscoverProbeTimeProvider? probeTimeProvider
    )
    {
        if (protocolVersion is not null)
        {
            return new McpClientOptions
            {
                ProtocolVersion = protocolVersion,
                // Disabling DiscoverProbeTimeout prevents ConnectAsync from cancelling `server/discover` early and
                // throwing before InitializationTimeout/inactivity timeouts expire on slow-starting 2026-07-28 servers.
                DiscoverProbeTimeout = Timeout.InfiniteTimeSpan,
                InitializationTimeout = isStdio ? _stdioMaxStartupTimeout : _httpStartupTimeout
            };
        }

        if (!isStdio)
        {
            return new McpClientOptions
            {
                InitializationTimeout = _httpStartupTimeout
            };
        }

        return new McpClientOptions
        {
            InitializationTimeout = _stdioMaxStartupTimeout,
            DiscoverProbeTimeout = _stdioDiscoverProbeTimeout,
            TimeProvider = probeTimeProvider ?? TimeProvider.System
        };
    }

    private static bool ShouldRetryWithJuly2026Protocol(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is McpProtocolException { ErrorCode: McpErrorCode.UnsupportedProtocolVersion })
            {
                return true;
            }
        }

        return false;
    }

    private HandledException CreateStartupTimeoutException(bool isStdio, bool isMaxLimit, Exception innerException)
    {
        var displayName = string.IsNullOrWhiteSpace(_transportConfiguration.Name)
            ? McpChatPlugin.Name
            : _transportConfiguration.Name;
        var timeoutException = isStdio
            ? isMaxLimit
                ? new TimeoutException(
                    $"MCP stdio startup for '{displayName}' exceeded maximum startup timeout ({_stdioMaxStartupTimeout.TotalMinutes:0.#}m).",
                    innerException)
                : new TimeoutException(
                    $"MCP stdio startup for '{displayName}' produced no output for {_stdioInactivityTimeout.TotalSeconds:0.#}s.",
                    innerException)
            : new TimeoutException(
                $"MCP HTTP connection to '{displayName}' timed out after {_httpStartupTimeout.TotalSeconds:0.#}s.",
                innerException);

        return new HandledException(
            timeoutException,
            new FormattedDynamicLocaleKey(
                LocaleKey.ChatPluginManager_Common_FailedToStartMcpPlugin,
                new DirectLocaleKey(displayName)),
            showDetails: true);
    }

    private static string FormatStatusLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length <= MaxStatusLineLength)
        {
            return trimmed;
        }

        return string.Concat(trimmed.AsSpan(0, MaxStatusLineLength - 1), "...");
    }

    private static bool IsClientHealthy(McpClient? client, Process? process)
    {
        return client is not null && !client.Completion.IsCompleted && process is not { HasExited: true };
    }

    private void UpdateFunctions(ManagedMcpClientTool[] tools)
    {
        McpChatPlugin.EditFunctions(list =>
        {
            var i = 0;
            var j = 0;
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

    private void HandleMcpProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process process) return;

        process.Exited -= HandleMcpProcessExited;
        CloseExitedProcessAsync(process).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    private async Task CloseExitedProcessAsync(Process process)
    {
        if (IsDisposed) return;

        await _connectionLock.WaitAsync();
        try
        {
            // Ignore delayed exit notifications from a process that has already been superseded.
            if (!IsDisposed && ReferenceEquals(process, _mcpProcess))
            {
                await CloseConnectionAsync(_mcpClient, shouldKillProcess: false);
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private void SaveCachedTools(IList<ManagedMcpClientTool> tools)
    {
        var json = JsonSerializer.Serialize(
            tools.Select(static t => new SerializableTool(t.Name, t.ProtocolTool)),
            SerializableToolJsonSerializerContext.Default.IEnumerableSerializableTool);
        _keyValueStorage.Set(GetToolCacheKey(McpChatPlugin), json);
    }

    private IEnumerable<ManagedMcpClientTool> LoadCachedTools()
    {
        var json = _keyValueStorage.Get<string>(GetToolCacheKey(McpChatPlugin));
        if (json is null) return [];

        try
        {
            var tools = JsonSerializer.Deserialize(json, SerializableToolJsonSerializerContext.Default.IEnumerableSerializableTool);
            return tools?.Select(t => new ManagedMcpClientTool(t.Tool, this, t.Name)) ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize cached tools for plugin {PluginName}.", McpChatPlugin.Name);
            return [];
        }
    }

    /// <summary>
    /// Invokes a tool on the active MCP session, transparently reconnecting once if an HTTP session expired.
    /// </summary>
    public async ValueTask<object?> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken
    )
    {
        var client = await EnsureConnectedAsync(failedClient: null, onConnecting: null, progress: null, cancellationToken);

        try
        {
            return await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ShouldReconnectAfterFailure(client, ex))
        {
            _logger.LogWarning(ex, "Session expired while calling {ToolName} on {PluginName}, reconnecting...", toolName, McpChatPlugin.Name);

            client = await EnsureConnectedAsync(failedClient: client, onConnecting: null, progress: null, cancellationToken);
            return await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
        }
    }

    private bool ShouldReconnectAfterFailure(McpClient client, Exception exception)
    {
        if (_transportConfiguration is not HttpMcpTransportConfiguration)
        {
            return false;
        }

        if (client.Completion is
            {
                IsCompletedSuccessfully: true,
                Result: HttpClientCompletionDetails { HttpStatusCode: HttpStatusCode.NotFound }
            })
        {
            return true;
        }

        return exception is HttpRequestException { StatusCode: HttpStatusCode.NotFound } ||
            exception.Data["ModelContextProtocol.HttpStatusCode"] is HttpStatusCode.NotFound;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        McpChatPlugin.IsRunning = false;
        try
        {
            await _lifetime.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
        }

        await _connectionLock.WaitAsync();
        try
        {
            await CloseConnectionAsync(_mcpClient);
        }
        finally
        {
            _lifetime.Dispose();
            _connectionLock.Release();
        }
    }

    private async ValueTask CloseConnectionAsync(McpClient? client, bool shouldKillProcess = true)
    {
        if (_mcpProcess is not null)
        {
            _mcpProcess.Exited -= HandleMcpProcessExited;
            _mcpProcess = null;
        }

        _mcpClient = null;
        McpChatPlugin.IsRunning = false;

        var registration = Interlocked.Exchange(ref _watchdogRegistration, null);
        try
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }
        }
        finally
        {
            if (registration is not null)
            {
                await registration.DisposeAsync(killOnDispose: shouldKillProcess);
            }
        }
    }

    private async Task<IList<ManagedMcpClientTool>> ListToolsAsync(McpClient client, CancellationToken cancellationToken)
    {
        var rawTools = await client.ListToolsAsync(cancellationToken: cancellationToken);

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

    private static readonly SearchValues<char> AllowedMcpIdentifierChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_");

    /// <summary>
    /// MCP tool names allow arbitrary characters beyond provider wire identifiers, so replace any
    /// character outside <c>[a-zA-Z0-9_]</c> (including <c>-</c> and <c>.</c> for stable rule keys) with <c>_</c>.
    /// </summary>
    private static string EscapeToolName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "tool";
        if (!name.AsSpan().ContainsAnyExcept(AllowedMcpIdentifierChars)) return name;

        return string.Create(
            name.Length,
            name,
            static (destination, source) =>
            {
                for (var i = 0; i < source.Length; i++)
                {
                    var ch = source[i];
                    destination[i] = AllowedMcpIdentifierChars.Contains(ch) ? ch : '_';
                }
            });
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
                    StandardErrorLines = HandleStdioStderrLine
                },
                _loggerFactory),
            HttpMcpTransportConfiguration sse => new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = sse.Name,
                    Endpoint = new Uri(sse.Endpoint, UriKind.Absolute),
                    ConnectionTimeout = _httpStartupTimeout,
                    AdditionalHeaders = sse.Headers
                        .AsValueEnumerable()
                        .Where(kv => !kv.Key.IsNullOrWhiteSpace() && !kv.Value.IsNullOrWhiteSpace())
                        .DistinctBy(kv => kv.Key)
                        .ToDictionary(kv => kv.Key, kv => kv.Value),
                    TransportMode = sse.TransportMode
                },
                _httpClientFactory.CreateClient(McpServiceExtensions.McpClientName),
                _loggerFactory,
                ownsHttpClient: true),
            _ => throw new InvalidOperationException("Unsupported MCP transport configuration type.")
        };
    }

    private void HandleStdioStderrLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        LogMcpStderrLine(line);
        _activeStartupStderrHandler?.Invoke(line);
    }

    private async Task RegisterStdioWatchdogAsync(McpClient client, IClientTransport clientTransport)
    {
        if (clientTransport is not StdioClientTransport) return;

        var processId = -1;
        var hasLoggedWarning = false;
        try
        {
            var transport = GetMcpClientTransport(client);
            if (GetStdioClientSessionTransportProcess(transport) is { HasExited: false, Id: > 0 } process)
            {
                _mcpProcess = process;
                _watchdogRegistration = await _watchdogManager.RegisterProcessAsync(process);
                process.Exited += HandleMcpProcessExited;
                processId = process.Id;

                if (process.HasExited)
                {
                    HandleMcpProcessExited(process, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex) when (_transportConfiguration is StdioMcpTransportConfiguration stdio)
        {
            hasLoggedWarning = true;
            _logger.LogWarning(
                ex,
                "Failed to inspect or register stdio process for MCP watchdog. Command: {Command}, Arguments: {Arguments}",
                stdio.Command,
                stdio.Arguments);
        }
        finally
        {
            if (!hasLoggedWarning && processId == -1 && _transportConfiguration is StdioMcpTransportConfiguration stdio)
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
        return Directory.Exists(workingDirectory)
            ? workingDirectory
            : RuntimeConstants.EnsureWritableDataFolderPath("plugins", "mcp", McpChatPlugin.Id.ToString("N"));
    }

    private static Dictionary<string, string?> EnsureLatestPath(
        Dictionary<string, string?> environmentVariables,
        IReadOnlyList<string> managedPathEntries
    )
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
        object client
    );

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_process")]
    private static extern ref Process GetStdioClientSessionTransportProcess(
        [UnsafeAccessorType("ModelContextProtocol.Client.StdioClientSessionTransport, ModelContextProtocol.Core")]
        object transport
    );

    /// <summary>
    /// Extends the MCP SDK's fixed <c>server/discover</c> probe timeout while a stdio server continues
    /// emitting startup stderr output, bounded by maxProbeWindow.
    /// </summary>
    /// <remarks>
    /// WORKAROUND (ModelContextProtocol 2.4.0, upstream issue #1954-style stdio cold-start race):
    /// <c>McpClient.CreateAsync</c> starts <see cref="McpClientOptions.DiscoverProbeTimeout"/> (default 5s)
    /// at process spawn without observing stderr startup progress. Re-check on future <c>ModelContextProtocol</c> upgrades.
    /// </remarks>
    private sealed class StdioDiscoverProbeTimeProvider(TimeSpan probeTimeout, TimeSpan maxProbeWindow, TimeProvider? inner = null) : TimeProvider
    {
        private readonly TimeProvider _inner = inner ?? System;
        private readonly Lock _lock = new();

        private ProbeTimer? _probeTimer;
        private long _probeStartTimestamp;
        private bool _isPaused;
        private bool _isDisposed;

        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

        public override long GetTimestamp() => _inner.GetTimestamp();

        public override System.Threading.ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var innerTimer = _inner.CreateTimer(callback, state, dueTime, period);
            if (period != Timeout.InfiniteTimeSpan || dueTime != probeTimeout || probeTimeout <= TimeSpan.Zero)
            {
                return innerTimer;
            }

            using (_lock.EnterScope())
            {
                if (_probeTimer is not null)
                {
                    return innerTimer;
                }

                _probeStartTimestamp = _inner.GetTimestamp();
                _probeTimer = new ProbeTimer(this, innerTimer);
                return _probeTimer;
            }
        }

        public void NotifyActivity()
        {
            using (_lock.EnterScope())
            {
                if (_probeTimer is null || _isPaused || _isDisposed)
                {
                    return;
                }

                var elapsed = _inner.GetElapsedTime(_probeStartTimestamp);
                var remainingWindow = maxProbeWindow - elapsed;
                if (remainingWindow <= TimeSpan.Zero)
                {
                    return;
                }

                var nextDueTime = remainingWindow < probeTimeout ? remainingWindow : probeTimeout;
                _probeTimer.ChangeCore(nextDueTime);
            }
        }

        private sealed class ProbeTimer(StdioDiscoverProbeTimeProvider owner, System.Threading.ITimer innerTimer) : System.Threading.ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                using (owner._lock.EnterScope())
                {
                    if (owner._isDisposed)
                    {
                        return false;
                    }

                    // RequestTimeout.Stop()/Suspend() passes InfiniteTimeSpan; honor that pause so stderr never revives a stopped timer.
                    owner._isPaused = dueTime == Timeout.InfiniteTimeSpan;
                    return innerTimer.Change(dueTime, period);
                }
            }

            public void ChangeCore(TimeSpan dueTime)
            {
                innerTimer.Change(dueTime, Timeout.InfiniteTimeSpan);
            }

            public void Dispose()
            {
                using (owner._lock.EnterScope())
                {
                    owner._isDisposed = true;
                    innerTimer.Dispose();
                }
            }

            public ValueTask DisposeAsync()
            {
                using (owner._lock.EnterScope())
                {
                    owner._isDisposed = true;
                    return innerTimer.DisposeAsync();
                }
            }
        }
    }

    /// <summary>
    /// Escaped name and protocol tool descriptor cached in key-value storage.
    /// </summary>
    private readonly record struct SerializableTool(string Name, Tool Tool);

    [JsonSerializable(typeof(IEnumerable<SerializableTool>))]
    private sealed partial class SerializableToolJsonSerializerContext : JsonSerializerContext;

    [LoggerMessage(LogLevel.Debug, "[MCP stderr] {Line}")]
    partial void LogMcpStderrLine(string line);
}
