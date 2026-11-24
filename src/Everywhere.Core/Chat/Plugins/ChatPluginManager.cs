using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Harness;
using Everywhere.Harness.Mcp;
using Everywhere.Utilities;
using FuzzySharp;
using Tabler.Avalonia;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ShadUI;

namespace Everywhere.Chat.Plugins;

public sealed class ChatPluginManager : IChatPluginManager, IAsyncDisposable
{
    private const string McpRuntimeWarningKey = "mcp.runtime";
    private const string McpStartupWarningKey = "mcp.startup";
    private const string McpStartupReminderKey = "mcp.startup";

    public IReadOnlyBindableList<BuiltInChatPlugin> BuiltInPlugins { get; }

    public IReadOnlyBindableList<McpChatPlugin> McpPlugins { get; }

    private readonly IServiceProvider _serviceProvider;
    private readonly Settings _settings;
    private readonly IRuntimeManager _runtimeManager;
    private readonly IKeyValueStorage? _keyValueStorage;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ChatPluginManager> _logger;

    private readonly ConcurrentDictionary<Guid, McpPluginSlot> _mcpPluginSlots = [];
    private readonly CompositeDisposable _disposables = new(3);
    private readonly SourceList<BuiltInChatPlugin> _builtInPluginsSource = new();
    private readonly SourceList<McpChatPlugin> _mcpPluginsSource = new();

    private bool _isDisposed;

    public ChatPluginManager(
        IServiceProvider serviceProvider,
        IEnumerable<BuiltInChatPlugin> builtInPlugins,
        Settings settings,
        IRuntimeManager runtimeManager,
        ILogger<ChatPluginManager> logger
    )
    {
        _serviceProvider = serviceProvider;
        _builtInPluginsSource.AddRange(builtInPlugins);
        _settings = settings;
        _runtimeManager = runtimeManager;
        _keyValueStorage = serviceProvider.GetService<IKeyValueStorage>();
        _loggerFactory = serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        _logger = logger;
        _runtimeManager.StatusChanged += HandleRuntimeManagerStatusChanged;

        // Load MCP plugins and restore their cached tool descriptors synchronously from in-memory KV storage.
        var mcpPlugins = ((IEnumerable<KeyValuePair<Guid, McpTransportConfiguration>>)settings.Plugin.McpChatPlugins)
            .AsValueEnumerable()
            .Select(static pair => new McpChatPlugin(pair.Key, pair.Value))
            .ToArray();
        foreach (var mcpPlugin in mcpPlugins)
        {
            var slot = new McpPluginSlot();
            try
            {
                slot.Client = CreateClient(mcpPlugin);
            }
            catch (Exception e)
            {
                logger.LogError(e, "An error occurred while initializing the MCP plugin {PluginId}.", mcpPlugin.Id);
            }

            _mcpPluginSlots[mcpPlugin.Id] = slot;
        }
        _mcpPluginsSource.AddRange(mcpPlugins);

        BuiltInPlugins = _builtInPluginsSource
            .Connect()
            .Filter(static p => p is { IsVisible: true, HasVisibleFunctions: true })
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);
        _disposables.Add(_builtInPluginsSource);

        McpPlugins = _mcpPluginsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);
        _disposables.Add(_mcpPluginsSource);

        RefreshMcpRuntimeWarnings();
    }

    public McpChatPlugin CreateMcpPlugin(McpTransportConfiguration configuration)
    {
        if (configuration.HasErrors)
        {
            throw new HandledException(
                new InvalidOperationException("MCP transport configuration is not valid."),
                new DynamicLocaleKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration));
        }

        var mcpChatPlugin = new McpChatPlugin(configuration);
        _mcpPluginSlots[mcpChatPlugin.Id] = new McpPluginSlot();
        _mcpPluginsSource.Add(mcpChatPlugin);
        _settings.Plugin.McpChatPlugins.Add(mcpChatPlugin.Id, configuration);
        UpdateMcpRuntimeWarning(mcpChatPlugin);
        return mcpChatPlugin;
    }

    public async Task UpdateMcpPluginAsync(McpChatPlugin mcpChatPlugin, McpTransportConfiguration configuration)
    {
        if (configuration.HasErrors)
        {
            throw new HandledException(
                new InvalidOperationException("MCP transport configuration is not valid."),
                new DynamicLocaleKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration));
        }

        var slot = _mcpPluginSlots.GetOrAdd(mcpChatPlugin.Id, static _ => new McpPluginSlot());
        var wasRunning = mcpChatPlugin.IsRunning;

        // Abort any in-flight handshake on this plugin before waiting for its slot gate so edits are responsive.
        slot.RequestShutdown();
        await slot.Gate.WaitAsync();
        try
        {
            if (slot.IsRemoved)
            {
                throw new InvalidOperationException("MCP plugin has been removed.");
            }

            // Retire any existing client even when stopped, because ManagedMcpClient captures
            // the previous TransportConfiguration and cached tools must not leak into the replacement.
            await slot.RetireClientAsync();

            ClearCachedTools(mcpChatPlugin);
            RemoveToolBypassApproval(mcpChatPlugin.Key);
            mcpChatPlugin.RemoveWarning(McpStartupWarningKey);
            mcpChatPlugin.TransportConfiguration = configuration;
            _settings.Plugin.McpChatPlugins[mcpChatPlugin.Id] = configuration;
            UpdateMcpRuntimeWarning(mcpChatPlugin);
        }
        finally
        {
            slot.Gate.Release();
        }

        if (wasRunning)
        {
            try
            {
                await StartMcpClientAsync(
                    mcpChatPlugin,
                    requireRegistered: true,
                    onConnecting: null,
                    progress: null,
                    CancellationToken.None);
            }
            catch (McpPluginRemovedException)
            {
                // Plugin was removed while the update lock was released before restarting.
            }
        }
    }

    public Task StartMcpClientAsync(
        McpChatPlugin mcpChatPlugin,
        IProgress<IDynamicLocaleKey>? progress = null,
        CancellationToken cancellationToken = default
    ) => StartMcpClientAsync(mcpChatPlugin, requireRegistered: false, onConnecting: null, progress, cancellationToken);

    private async Task StartMcpClientAsync(
        McpChatPlugin mcpChatPlugin,
        bool requireRegistered,
        Func<ValueTask>? onConnecting,
        IProgress<IDynamicLocaleKey>? progress,
        CancellationToken cancellationToken
    )
    {
        if (mcpChatPlugin.TransportConfiguration is not { } transportConfiguration || transportConfiguration.HasErrors)
        {
            throw new HandledException(
                new InvalidOperationException(
                    mcpChatPlugin.TransportConfiguration is null
                        ? "MCP transport configuration is not set."
                        : "MCP transport configuration is not valid."),
                new DynamicLocaleKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration));
        }

        McpPluginSlot slot;
        if (requireRegistered)
        {
            if (!_mcpPluginSlots.TryGetValue(mcpChatPlugin.Id, out slot!) || slot.IsRemoved)
            {
                throw new McpPluginRemovedException();
            }
        }
        else
        {
            slot = _mcpPluginSlots.GetOrAdd(mcpChatPlugin.Id, static _ => new McpPluginSlot());
        }

        await slot.Gate.WaitAsync(cancellationToken);
        try
        {
            if (slot.IsRemoved)
            {
                throw new McpPluginRemovedException();
            }

            var client = slot.Client ??= CreateClient(mcpChatPlugin);
            try
            {
                await client.StartAsync(onConnecting, progress, cancellationToken);
                mcpChatPlugin.RemoveWarning(McpStartupWarningKey);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                var handledFailure = ToHandledStartupException(mcpChatPlugin, ex);
                mcpChatPlugin.SetWarning(McpStartupWarningKey, handledFailure.FriendlyMessageKey);
                throw;
            }
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    public async Task StopMcpClientAsync(McpChatPlugin mcpChatPlugin)
    {
        if (!_mcpPluginSlots.TryGetValue(mcpChatPlugin.Id, out var slot))
        {
            return;
        }

        slot.RequestShutdown();
        await slot.Gate.WaitAsync();
        try
        {
            await slot.RetireClientAsync();
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    public async Task RemoveMcpPluginAsync(McpChatPlugin mcpChatPlugin)
    {
        if (_mcpPluginSlots.TryGetValue(mcpChatPlugin.Id, out var slot))
        {
            slot.RequestShutdown();
            await slot.Gate.WaitAsync();
            try
            {
                slot.MarkRemoved();
                _mcpPluginSlots.TryRemove(mcpChatPlugin.Id, out _);
                await slot.RetireClientAsync();
            }
            finally
            {
                slot.Gate.Release();
            }
        }

        _mcpPluginsSource.Remove(mcpChatPlugin);
        ClearCachedTools(mcpChatPlugin);
        _settings.Plugin.McpChatPlugins.Remove(mcpChatPlugin.Id);
        RemoveToolSettings(mcpChatPlugin.Key);
    }

    public RuntimeDependency? GetMissingRuntimeDependency(McpChatPlugin mcpChatPlugin)
    {
        return McpTransportPreconditions.GetMissingRuntimeDependency(_runtimeManager, mcpChatPlugin.TransportConfiguration);
    }

    public void RefreshMcpRuntimeWarnings()
    {
        foreach (var mcpPlugin in _mcpPluginsSource.Items)
        {
            UpdateMcpRuntimeWarning(mcpPlugin);
        }
    }

    public async Task<IChatPluginScope> CreateScopeAsync(
        AssistantConfiguration configuration,
        ChatContext chatContext,
        ChatMessageNode ownerNode,
        IToolRulesets? toolRulesets,
        CancellationToken cancellationToken
    )
    {
        BuiltInChatPlugin[] builtInPlugins = [];
        _builtInPluginsSource.Edit(list =>
            builtInPlugins = [.. list.OrderBy(static plugin => plugin.Key, StringComparer.Ordinal)]);

        McpChatPlugin[] mcpPlugins = [];
        _mcpPluginsSource.Edit(list => mcpPlugins = [.. list]);

        var enabledBuiltIns = new List<BuiltInChatPlugin>(builtInPlugins.Length);
        foreach (var builtInPlugin in builtInPlugins)
        {
            if (IsPluginAllowed(builtInPlugin, toolRulesets))
            {
                enabledBuiltIns.Add(builtInPlugin);
            }
        }

        var enabledMcps = new List<McpChatPlugin>(mcpPlugins.Length);
        foreach (var mcpPlugin in mcpPlugins)
        {
            if (IsPluginAllowed(mcpPlugin, toolRulesets))
            {
                enabledMcps.Add(mcpPlugin);
            }
        }

        var startupErrors = new ConcurrentDictionary<Guid, HandledException>();
        var removedMcpIds = new ConcurrentDictionary<Guid, byte>();

        if (enabledMcps.Count > 0)
        {
            await Task.WhenAll(
                enabledMcps.Select(mcpPlugin => StartMcpForScopeAsync(
                    mcpPlugin,
                    chatContext,
                    ownerNode,
                    startupErrors,
                    removedMcpIds,
                    cancellationToken)));
        }

        var pluginNameDeduplicator = new NumberedDeduplicator();
        var functionNameDeduplicator = new NumberedDeduplicator();
        var resultPlugins = new List<ChatPluginSnapshot>(enabledBuiltIns.Count + enabledMcps.Count);
        var failedReminders = new List<(string Name, string Reason)>();

        foreach (var builtInPlugin in enabledBuiltIns)
        {
            var actualFunctions = FilterFunctions(builtInPlugin, configuration, toolRulesets);
            if (actualFunctions.Length > 0)
            {
                resultPlugins.Add(
                    builtInPlugin.CreateSnapshot(pluginNameDeduplicator, functionNameDeduplicator, actualFunctions));
            }
        }

        foreach (var mcpPlugin in enabledMcps)
        {
            if (removedMcpIds.ContainsKey(mcpPlugin.Id))
            {
                continue;
            }

            if (startupErrors.TryGetValue(mcpPlugin.Id, out var startupError))
            {
                failedReminders.Add((GetPluginDisplayName(mcpPlugin), FormatStartupFailureReminderReason(startupError)));
                continue;
            }

            var actualFunctions = FilterFunctions(mcpPlugin, configuration, toolRulesets);
            resultPlugins.Add(mcpPlugin.CreateSnapshot(pluginNameDeduplicator, functionNameDeduplicator, actualFunctions));
        }

        var transientReminders = BuildStartupFailureReminders(failedReminders);
        return new ChatPluginScope(resultPlugins, transientReminders);
    }

    private ChatFunction[] FilterFunctions(
        ChatPlugin plugin,
        AssistantConfiguration configuration,
        IToolRulesets? toolRulesets
    )
    {
        return plugin.GetChatFunctions()
            .AsValueEnumerable()
            .Where(function => configuration.InputModalities.HasFlag(function.RequiredModalities))
            .Where(function => toolRulesets?.GetFunctionRule(plugin, function) ??
                _settings.Plugin.ToolEnablementRulesets.GetFunctionRule(plugin, function) ??
                function.IsDefaultEnabled)
            .ToArray();
    }

    private static IReadOnlyList<ContextTransientReminder> BuildStartupFailureReminders(List<(string Name, string Reason)> failedPlugins)
    {
        if (failedPlugins.Count == 0)
        {
            return [];
        }

        failedPlugins.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        var lines = failedPlugins.Select(static entry => $"- {entry.Name}: {entry.Reason}");
        var message =
            "The following enabled MCP plugins failed to start for this turn and their tools are unavailable " +
            $"(do not retry them in this turn):\n{string.Join('\n', lines)}";
        return
        [
            new ContextTransientReminder(
                McpStartupReminderKey,
                message,
                "Previously reported MCP startup failures no longer apply to the current active toolset.")
        ];
    }

    private static string FormatStartupFailureReminderReason(HandledException startupError)
    {
        foreach (var error in startupError.Segregate())
        {
            switch (error)
            {
                case TimeoutException:
                    return "startup timed out";
                case ClientTransportClosedException closed when McpTransportPreconditions.IsWindowsCommandNotFound(closed):
                    return "required command or runtime not found";
                case ClientTransportClosedException:
                    return "server process exited unexpectedly";
                case Win32Exception or FileNotFoundException or DirectoryNotFoundException:
                    return "required command or runtime not found";
                case HttpRequestException or SocketException:
                    return "connection failed";
                case McpProtocolException:
                    return "protocol handshake failed";
            }
        }

        return "startup failed (check plugin settings for details)";
    }

    private async Task StartMcpForScopeAsync(
        McpChatPlugin mcpChatPlugin,
        ChatContext chatContext,
        ChatMessageNode ownerNode,
        ConcurrentDictionary<Guid, HandledException> startupErrors,
        ConcurrentDictionary<Guid, byte> removedMcpIds,
        CancellationToken cancellationToken
    )
    {
        IBusyActivity? activity = null;
        try
        {
            await StartMcpClientAsync(
                mcpChatPlugin,
                requireRegistered: true,
                onConnecting: async () =>
                {
                    if (activity is not null) return;

                    var headerKey = new FormattedDynamicLocaleKey(
                        LocaleKey.ChatContext_BusyMessage_StartingMcp,
                        new DirectLocaleKey(GetPluginDisplayName(mcpChatPlugin)));
                    activity = await chatContext.DispatchAsync(() => chatContext.Data.StartActivity(
                        TablerIconKind.Mcp,
                        headerKey,
                        removeAfterCompletion: true,
                        owner: ownerNode.Message));
                },
                new StartupStatusProgress(statusKey =>
                {
                    if (activity is { } currentActivity)
                    {
                        chatContext.DispatchAsync(() => currentActivity.SecondaryHeaderKey = statusKey)
                            .Detach(IExceptionHandler.DangerouslyIgnoreAllException);
                    }
                }),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (McpPluginRemovedException)
        {
            removedMcpIds[mcpChatPlugin.Id] = 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP plugin {PluginName} ({PluginId}) failed to start for chat scope.", mcpChatPlugin.Name, mcpChatPlugin.Id);
            startupErrors[mcpChatPlugin.Id] = ToHandledStartupException(mcpChatPlugin, ex);
        }
        finally
        {
            if (activity is { } createdActivity)
            {
                try
                {
                    await chatContext.DispatchAsync(createdActivity.Dispose);
                }
                catch
                {
                    // Do not mask the primary startup result if disposing the activity fails during shutdown.
                }
            }
        }
    }

    private static HandledException ToHandledStartupException(McpChatPlugin mcpChatPlugin, Exception exception)
    {
        return exception as HandledException ?? new HandledException(
            exception,
            new FormattedDynamicLocaleKey(
                LocaleKey.ChatPluginManager_Common_FailedToStartMcpPlugin,
                new DirectLocaleKey(GetPluginDisplayName(mcpChatPlugin))),
            showDetails: true);
    }

    private static string GetPluginDisplayName(McpChatPlugin mcpChatPlugin)
    {
        var configuredName = mcpChatPlugin.TransportConfiguration?.Name;
        return string.IsNullOrWhiteSpace(configuredName) ? mcpChatPlugin.Name : configuredName;
    }

    private bool IsPluginAllowed(ChatPlugin plugin, IToolRulesets? toolRulesets)
    {
        return toolRulesets?.GetPluginRule(plugin) ??
            _settings.Plugin.ToolEnablementRulesets.GetPluginRule(plugin) ??
            plugin.IsDefaultEnabled;
    }

    private ManagedMcpClient CreateClient(McpChatPlugin mcpChatPlugin)
    {
        return new ManagedMcpClient(
            mcpChatPlugin,
            _serviceProvider,
            new McpLoggerFactory(mcpChatPlugin, _loggerFactory),
            () => UpdateMcpRuntimeWarning(mcpChatPlugin));
    }

    private void ClearCachedTools(McpChatPlugin mcpChatPlugin)
    {
        if (_keyValueStorage is not null)
        {
            ManagedMcpClient.ClearCachedTools(_keyValueStorage, mcpChatPlugin);
        }
        else
        {
            mcpChatPlugin.EditFunctions(static list => list.Clear());
        }
    }

    private void RemoveToolSettings(string pluginKey)
    {
        var prefix = ToolSettingsKey.ForFunctionPrefix(pluginKey);
        RemoveRecords(_settings.Plugin.ToolEnablementRulesets);
        RemoveRecords(_settings.Plugin.ToolBypassApprovalRulesets);
        foreach (var assistant in _settings.Model.CustomAssistants)
        {
            if (assistant.ToolEnablementRulesets is { } overrides) RemoveRecords(overrides);
        }

        void RemoveRecords(IDictionary<string, bool> records)
        {
            records.Remove(ToolSettingsKey.ForPlugin(pluginKey));
            foreach (var key in records.Keys.AsValueEnumerable().Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                records.Remove(key);
            }
        }
    }

    private void RemoveToolBypassApproval(string pluginKey)
    {
        ToolBypassApprovalPolicy.RemovePluginRules(_settings.Plugin.ToolBypassApprovalRulesets, pluginKey);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _runtimeManager.StatusChanged -= HandleRuntimeManagerStatusChanged;

        var slots = _mcpPluginSlots.Values.ToArray();
        _mcpPluginSlots.Clear();

        foreach (var slot in slots)
        {
            slot.MarkRemoved();
            slot.RequestShutdown();
        }

        _mcpPluginsSource.Clear();
        _disposables.Dispose();

        await Task.WhenAll(slots.Select(static slot => slot.DisposeAsync().AsTask()));
    }

    private void HandleRuntimeManagerStatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.PostOnDemand(RefreshMcpRuntimeWarnings);
    }

    private void UpdateMcpRuntimeWarning(McpChatPlugin mcpChatPlugin)
    {
        var missingDependency = GetMissingRuntimeDependency(mcpChatPlugin);
        if (missingDependency is null)
        {
            mcpChatPlugin.RemoveWarning(McpRuntimeWarningKey);
            return;
        }

        mcpChatPlugin.SetWarning(
            McpRuntimeWarningKey,
            new FormattedDynamicLocaleKey(
                LocaleKey.ChatPluginManager_McpPluginMissingRuntime_Warning,
                new DirectLocaleKey(missingDependency.DisplayName)),
            new AsyncRelayCommand<ToastResult>(result => ResolveMcpRuntimeDependencyAsync(mcpChatPlugin, missingDependency, result)));
    }

    private async Task ResolveMcpRuntimeDependencyAsync(
        McpChatPlugin mcpChatPlugin,
        RuntimeDependency dependency,
        ToastResult? toastResult
    )
    {
        if (toastResult != ToastResult.ActionButtonClicked) return;

        try
        {
            if (dependency.Kind == RuntimeKind.Docker)
            {
                await App.Launcher.LaunchUriAsync(LinkConstants.DockerInstallGuideUri);
                return;
            }

            var progress = new Progress<double>();
            var cancellationTokenSource = new CancellationTokenSource();
            ToastManager
                .Create(LocaleKey.Common_Info.I18N())
                .WithContent(LocaleKey.RuntimeManager_InstallRuntime_Toast_Content.I18N().Format(dependency.DisplayName))
                .WithProgress(progress)
                .WithCancellationTokenSource(cancellationTokenSource)
                .OnBottomRight()
                .ShowInfo();

            await _runtimeManager.InstallAsync(dependency.Kind, progress, cancellationTokenSource.Token);
            RefreshMcpRuntimeWarnings();

            ToastManager.Success(LocaleKey.RuntimeManager_InstallRuntime_SuccessToast_Title.I18N().Format(dependency.DisplayName));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to resolve runtime dependency {RuntimeKind} for MCP plugin {PluginId}.", dependency.Kind, mcpChatPlugin.Id);
            ToastManager.Error($"[{nameof(ChatPluginManager)}] Failed to resolve runtime dependency", e.GetFriendlyMessage());
        }
    }

    private sealed class ChatPluginScope : IChatPluginScope
    {
        public IReadOnlyList<ChatPluginSnapshot> Plugins { get; }

        public IReadOnlyList<AIFunctionDeclaration> Declarations { get; }

        public IReadOnlyList<ContextTransientReminder> TransientReminders { get; }

        private readonly Dictionary<string, ToolBinding> _bindings;

        public ChatPluginScope(List<ChatPluginSnapshot> plugins, IReadOnlyList<ContextTransientReminder> transientReminders)
        {
            Plugins = plugins;
            TransientReminders = transientReminders;

            var count = plugins.AsValueEnumerable().Sum(plugin => plugin.FunctionCount);
            _bindings = new Dictionary<string, ToolBinding>(count, StringComparer.Ordinal);
            var declarations = new AIFunctionDeclaration[count];
            var index = 0;
            foreach (var plugin in plugins)
            {
                foreach (var binding in plugin.Bindings)
                {
                    _bindings.Add(binding.Declaration.Name, binding);
                    declarations[index++] = binding.Declaration;
                }
            }

            Array.Sort(declarations, static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            Declarations = declarations;
        }

        public bool TryGetBinding(
            string functionName,
            [NotNullWhen(true)] out ToolBinding? binding,
            [NotNullWhen(false)] out IReadOnlyList<string>? similarFunctionNames
        )
        {
            if (_bindings.TryGetValue(functionName, out binding))
            {
                similarFunctionNames = null;
                return true;
            }

            similarFunctionNames =
            [
                .. Process.ExtractTop(functionName, _bindings.Keys, limit: 5)
                    .Where(r => r.Score >= 60)
                    .Select(r => r.Value),
            ];
            return false;
        }
    }

    private sealed class StartupStatusProgress(Action<IDynamicLocaleKey> report) : IProgress<IDynamicLocaleKey>
    {
        public void Report(IDynamicLocaleKey value) => report(value);
    }

    private sealed class McpPluginRemovedException : InvalidOperationException;

    /// <summary>
    /// Serializes lifecycle operations (start, stop, update, removal) for a single MCP plugin without
    /// blocking unrelated servers.
    /// </summary>
    private sealed class McpPluginSlot : IAsyncDisposable
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ManagedMcpClient? Client
        {
            get => Volatile.Read(ref _client);
            set => Volatile.Write(ref _client, value);
        }

        public bool IsRemoved => Volatile.Read(ref _isRemoved) != 0;

        private ManagedMcpClient? _client;
        private int _isRemoved;

        public void RequestShutdown()
        {
            Volatile.Read(ref _client)?.RequestShutdown();
        }

        public void MarkRemoved()
        {
            Interlocked.Exchange(ref _isRemoved, 1);
        }

        public async ValueTask RetireClientAsync()
        {
            var client = Interlocked.Exchange(ref _client, null);
            if (client is not null)
            {
                await client.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            RequestShutdown();
            await Gate.WaitAsync();
            try
            {
                await RetireClientAsync();
            }
            finally
            {
                Gate.Release();
            }
        }
    }

    /// <summary>
    /// Used to create ILogger instances for MCP clients.
    /// Logs to both the Everywhere logging system and the <see cref="McpChatPlugin"/>'s log entries.
    /// </summary>
    private sealed class McpLoggerFactory(McpChatPlugin mcpChatPlugin, ILoggerFactory innerLoggerFactory) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) => innerLoggerFactory.AddProvider(provider);

        public ILogger CreateLogger(string categoryName)
        {
            var innerLogger = innerLoggerFactory.CreateLogger(categoryName);
            return new McpLogger(mcpChatPlugin, innerLogger);
        }

        public void Dispose()
        {
            // Owned by DI; each ManagedMcpClient's factory wrapper must not dispose the shared inner factory.
        }

        private sealed class McpLogger(ILogger mcpChatPlugin, ILogger innerLogger) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => innerLogger.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                mcpChatPlugin.Log(logLevel, eventId, state, exception, formatter);
                innerLogger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
