using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using Everywhere.AI;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins.Mcp;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Utilities;
using FuzzySharp;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using ShadUI;
using ZLinq;

namespace Everywhere.Chat.Plugins;

public class ChatPluginManager : IChatPluginManager
{
    private const string McpRuntimeWarningKey = "mcp.runtime";

    public IReadOnlyBindableList<BuiltInChatPlugin> BuiltInPlugins { get; }

    public IReadOnlyBindableList<McpChatPlugin> McpPlugins { get; }

    private readonly IServiceProvider _serviceProvider;
    private readonly Settings _settings;
    private readonly IRuntimeManager _runtimeManager;
    private readonly ILogger<ChatPluginManager> _logger;

    private readonly ConcurrentDictionary<Guid, ManagedMcpClient> _managedClients = [];
    private readonly CompositeDisposable _disposables = new(3);
    private readonly SourceList<BuiltInChatPlugin> _builtInPluginsSource = new();
    private readonly SourceList<McpChatPlugin> _mcpPluginsSource = new();
    private readonly ObjectObserver _builtInPluginsObserver;
    private readonly ObjectObserver _mcpPluginsObserver;

    public ChatPluginManager(
        IServiceProvider serviceProvider,
        IEnumerable<BuiltInChatPlugin> builtInPlugins,
        Settings settings,
        IRuntimeManager runtimeManager,
        ILogger<ChatPluginManager> logger)
    {
        _serviceProvider = serviceProvider;
        _builtInPluginsSource.AddRange(builtInPlugins);
        _settings = settings;
        _runtimeManager = runtimeManager;
        _logger = logger;
        _runtimeManager.StatusChanged += HandleRuntimeManagerStatusChanged;

        // Load MCP plugins from settings.
        var mcpPlugins = settings.Plugin.McpChatPlugins.AsValueEnumerable().Select(m => m.ToMcpChatPlugin()).OfType<McpChatPlugin>().ToList();
        Task.Run(InitializeMcpPlugins).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        _mcpPluginsSource.AddRange(mcpPlugins);

        // Apply the enabled state from settings.
        var isEnabledRecords = settings.Plugin.IsEnabledRecords;
        var isPermissionGrantedRecords = settings.Plugin.IsPermissionGrantedRecords;
        var pluginKeys = new HashSet<string>();
        foreach (var plugin in _builtInPluginsSource.Items.AsValueEnumerable().OfType<ChatPlugin>().Concat(_mcpPluginsSource.Items))
        {
            pluginKeys.Add(plugin.Key);
            plugin.IsEnabled = GetIsEnabled(plugin.Key, plugin is BuiltInChatPlugin { IsDefaultEnabled: true });
            foreach (var function in plugin.GetChatFunctions().AsValueEnumerable())
            {
                var key = $"{plugin.Key}.{function.KernelFunction.Name}";
                function.IsEnabled = GetIsEnabled(key, true);
                function.AutoApprove = function.IsAutoApproveAllowed && GetIsPermissionGranted(key, function.Permissions);
            }
        }

        // Remove any records in settings that do not correspond to any existing plugin.
        foreach (var key in isEnabledRecords.Keys.AsValueEnumerable()
                     .Where(key => pluginKeys.All(k => k != key && !key.StartsWith($"{k}.", StringComparison.Ordinal))).ToList())
        {
            isEnabledRecords.Remove(key);
        }
        foreach (var key in isPermissionGrantedRecords.Keys.AsValueEnumerable()
                     .Where(key => pluginKeys.All(k => k != key && !key.StartsWith($"{k}.", StringComparison.Ordinal))).ToList())
        {
            isPermissionGrantedRecords.Remove(key);
        }

        BuiltInPlugins = _builtInPluginsSource
            .Connect()
            .Filter(p => p is { IsVisible: true, HasVisibleFunctions: true })
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);
        _disposables.Add(_builtInPluginsSource);

        McpPlugins = _mcpPluginsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);
        _disposables.Add(_mcpPluginsSource);

        settings.Plugin.McpChatPlugins = _mcpPluginsSource
            .Connect()
            .AutoRefresh(m => m.TransportConfiguration)
            .ObserveOnAvaloniaDispatcher()
            .Transform(m => new McpChatPluginEntity(m), transformOnRefresh: true)
            .BindEx(_disposables);

        _builtInPluginsObserver = new ObjectObserver((in e) => HandleChatPluginChanged(BuiltInPlugins, e)).Observe(BuiltInPlugins);
        _mcpPluginsObserver = new ObjectObserver((in e) => HandleChatPluginChanged(McpPlugins, e)).Observe(McpPlugins);
        RefreshMcpRuntimeWarnings();

        void InitializeMcpPlugins()
        {
            foreach (var mcpPlugin in mcpPlugins)
            {
                try
                {
                    GetOrCreateClient(mcpPlugin);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "An error occured while initializing the MCP plugin");
                }
            }
        }

        // Helper method to get the enabled state from settings.
        bool GetIsEnabled(string path, bool defaultValue)
        {
            return isEnabledRecords.TryGetValue(path, out var isEnabled) ? isEnabled : defaultValue;
        }

        bool GetIsPermissionGranted(string path, ChatFunctionPermissions permissions)
        {
            if (isPermissionGrantedRecords.TryGetValue(path, out var isGranted) && !isGranted) return false;
            if (isGranted) return true;
            return permissions <= ChatFunctionPermissions.AutoGranted;
        }

        // Handle changes to plugins and update settings accordingly.
        void HandleChatPluginChanged<TPlugin>(IReadOnlyList<TPlugin> plugins, in ObjectObserverChangedEventArgs e) where TPlugin : ChatPlugin
        {
            var parts = e.Path.Split(':');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var pluginIndex) || pluginIndex < 0 || pluginIndex >= plugins.Count)
            {
                return;
            }

            var plugin = plugins[pluginIndex];
            var value = e.Value is true;

            ObservableDictionary<string, bool> records;
            bool? defaultValue;
            if (e.Path.EndsWith(nameof(ChatFunction.IsEnabled), StringComparison.Ordinal))
            {
                records = isEnabledRecords;
                defaultValue = parts.Length != 2 || plugin is BuiltInChatPlugin { IsDefaultEnabled: true };
            }
            else if (e.Path.EndsWith(nameof(ChatFunction.AutoApprove), StringComparison.Ordinal))
            {
                records = isPermissionGrantedRecords;
                defaultValue = null;
            }
            else
            {
                return;
            }

            string key;
            switch (parts.Length)
            {
                case 2:
                {
                    key = plugin.Key;
                    break;
                }
                case 4 when
                    int.TryParse(parts[2], out var functionIndex) &&
                    functionIndex >= 0 &&
                    functionIndex < plugin.Functions.Count:
                {
                    var observedFunction = plugin.Functions[functionIndex];
                    var function = plugin.GetChatFunctions()
                        .AsValueEnumerable()
                        .FirstOrDefault(f => ReferenceEquals(f, observedFunction)) ?? observedFunction;
                    key = $"{plugin.Key}.{function.KernelFunction.Name}";
                    break;
                }
                default:
                {
                    throw new InvalidOperationException($"Unexpected change path: {e.Path}");
                }
            }

            if (value == defaultValue) records.Remove(key);
            else records[key] = value;
        }
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
        GetOrCreateClient(mcpChatPlugin);
        _mcpPluginsSource.Add(mcpChatPlugin);
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

        var wasRunning = mcpChatPlugin.IsRunning;
        if (wasRunning)
        {
            await StopMcpClientAsync(mcpChatPlugin);
        }

        mcpChatPlugin.TransportConfiguration = configuration;
        UpdateMcpRuntimeWarning(mcpChatPlugin);

        if (wasRunning)
        {
            await StartMcpClientAsync(mcpChatPlugin, CancellationToken.None);
        }
    }

    private ManagedMcpClient GetOrCreateClient(McpChatPlugin mcpChatPlugin)
    {
        if (_managedClients.TryGetValue(mcpChatPlugin.Id, out var existingClient))
        {
            return existingClient;
        }

        var client = new ManagedMcpClient(
            mcpChatPlugin,
            this,
            _serviceProvider,
            new McpLoggerFactory(mcpChatPlugin, _serviceProvider.GetRequiredService<ILoggerFactory>()),
            _settings.Plugin);

        _managedClients[mcpChatPlugin.Id] = client;
        return client;
    }

    public async Task StartMcpClientAsync(McpChatPlugin mcpChatPlugin, CancellationToken cancellationToken)
    {
        if (mcpChatPlugin.TransportConfiguration is not { } transportConfiguration)
        {
            throw new HandledException(
                new InvalidOperationException("MCP transport configuration is not set."),
                new DynamicLocaleKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration));
        }

        if (transportConfiguration.HasErrors)
        {
            throw new HandledException(
                new InvalidOperationException("MCP transport configuration is not valid."),
                new DynamicLocaleKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration));
        }

        await EnsureMcpStartPreconditionsAsync(mcpChatPlugin, transportConfiguration, cancellationToken);

        var client = GetOrCreateClient(mcpChatPlugin);
        try
        {
            await client.StartAsync(cancellationToken);
        }
        catch (Exception ex) when (TryCreateMcpStartHandledException(transportConfiguration, ex, out var handledException))
        {
            throw handledException;
        }
    }

    public async Task StopMcpClientAsync(McpChatPlugin mcpChatPlugin)
    {
        if (_managedClients.TryRemove(mcpChatPlugin.Id, out var runningClient))
        {
            await runningClient.DisposeAsync();
        }
    }

    public async Task RemoveMcpPluginAsync(McpChatPlugin mcpChatPlugin)
    {
        await StopMcpClientAsync(mcpChatPlugin);
        _mcpPluginsSource.Remove(mcpChatPlugin);
    }

    public RuntimeDependency? GetMissingRuntimeDependency(McpChatPlugin mcpChatPlugin)
    {
        return mcpChatPlugin.TransportConfiguration is StdioMcpTransportConfiguration stdio ?
            _runtimeManager.GetMissingDependency(stdio.Command) :
            null;
    }

    public void RefreshMcpRuntimeWarnings()
    {
        foreach (var mcpPlugin in _mcpPluginsSource.Items)
        {
            UpdateMcpRuntimeWarning(mcpPlugin);
        }
    }

    private async Task EnsureMcpStartPreconditionsAsync(
        McpChatPlugin mcpChatPlugin,
        McpTransportConfiguration transportConfiguration,
        CancellationToken cancellationToken)
    {
        if (transportConfiguration is not StdioMcpTransportConfiguration stdio) return;

        if (!_runtimeManager.HasRefreshed)
        {
            await _runtimeManager.RefreshAsync(cancellationToken);
        }

        UpdateMcpRuntimeWarning(mcpChatPlugin);
        if (GetMissingRuntimeDependency(mcpChatPlugin) is { } missingDependency)
        {
            throw new HandledException(
                new FileNotFoundException(
                    $"MCP stdio command '{stdio.Command}' requires missing runtime '{missingDependency.DisplayName}'.",
                    stdio.Command),
                new FormattedDynamicLocaleKey(
                    LocaleKey.ChatPluginManager_McpPluginMissingRuntime_StartFailure,
                    new DirectLocaleKey(missingDependency.DisplayName)));
        }

        var command = NormalizeCommand(stdio.Command);
        if (command.IsNullOrWhiteSpace()) return;
        if (IsStdioCommandAvailable(stdio, command)) return;

        throw new HandledException(
            new FileNotFoundException($"MCP stdio command '{command}' was not found.", command),
            new FormattedDynamicLocaleKey(
                LocaleKey.ChatPluginManager_McpPluginCommandNotFound_StartFailure,
                new DirectLocaleKey(command)));
    }

    private static bool TryCreateMcpStartHandledException(
        McpTransportConfiguration transportConfiguration,
        Exception exception,
        [NotNullWhen(true)] out HandledException? handledException)
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

    private bool IsStdioCommandAvailable(StdioMcpTransportConfiguration stdio, string command)
    {
        if (RuntimeDependencyDetector.LooksLikePath(command))
        {
            var commandPath = Path.IsPathFullyQualified(command) ?
                command :
                Path.GetFullPath(command, GetConfiguredWorkingDirectory(stdio));
            return File.Exists(commandPath);
        }

        return GetStdioPathEntries(stdio)
            .AsValueEnumerable()
            .Where(Directory.Exists)
            .Any(directory =>
                GetExecutableCandidates(command)
                    .AsValueEnumerable()
                    .Any(candidate => File.Exists(Path.Combine(directory, candidate))));
    }

    private IEnumerable<string> GetStdioPathEntries(StdioMcpTransportConfiguration stdio)
    {
        foreach (var path in _runtimeManager.GetPathEntries())
        {
            yield return path;
        }

        foreach (var path in SplitPath(EnvironmentVariableUtilities.GetLatestPathVariable()))
        {
            yield return path;
        }

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string? configuredPath = null;
        foreach (var kv in stdio.EnvironmentVariables.AsValueEnumerable())
        {
            if (!pathComparer.Equals(kv.Key, "PATH")) continue;
            configuredPath = kv.Value;
            break;
        }

        foreach (var path in SplitPath(configuredPath))
        {
            yield return path;
        }
    }

    private static IEnumerable<string> SplitPath(string? path)
    {
        if (path.IsNullOrWhiteSpace()) yield break;

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return entry;
        }
    }

    private static IEnumerable<string> GetExecutableCandidates(string command)
    {
        yield return command;

        if (!OperatingSystem.IsWindows() || Path.HasExtension(command)) yield break;

        yield return command + ".exe";
        yield return command + ".cmd";
        yield return command + ".bat";
        yield return command + ".com";
    }

    private static string GetConfiguredWorkingDirectory(StdioMcpTransportConfiguration stdio)
    {
        return !stdio.WorkingDirectory.IsNullOrWhiteSpace() && Directory.Exists(stdio.WorkingDirectory) ?
            stdio.WorkingDirectory :
            Environment.CurrentDirectory;
    }

    private static string NormalizeCommand(string command) => command.Trim().Trim('"');

    public async Task<IChatPluginScope> CreateScopeAsync(
        Assistant assistant,
        ChatContext chatContext,
        ToolRulesets? toolRulesets,
        CancellationToken cancellationToken)
    {
        var pluginNameDeduplicator = new NumberedDeduplicator();
        var functionNameDeduplicator = new NumberedDeduplicator();
        var resultPlugins = new List<ChatPluginSnapshot>();
        IDisposable? startingMcpActivity = null;

        try
        {
            foreach (var plugin in _builtInPluginsSource.Items.Cast<ChatPlugin>().Concat(_mcpPluginsSource.Items))
            {
                // If toolRulesets?.IsPluginAllowed(plugin) returns null, follow plugin.IsEnabled
                // otherwise, follow toolRulesets?.IsPluginAllowed(plugin)
                // false || (null && false)
                var isPluginAllowed = toolRulesets?.IsPluginAllowed(plugin);
                if (isPluginAllowed is false || (isPluginAllowed is null && !plugin.IsEnabled)) continue;

                if (plugin is McpChatPlugin mcpChatPlugin)
                {
                    startingMcpActivity ??= chatContext.SetBusyActivity(
                        LucideIconKind.Server,
                        new DynamicLocaleKey(LocaleKey.ChatContext_BusyMessage_StartingMcp));

                    try
                    {
                        await StartMcpClientAsync(mcpChatPlugin, cancellationToken);
                    }
                    catch (HandledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw new HandledException(
                            ex,
                            new FormattedDynamicLocaleKey(
                                LocaleKey.ChatPluginManager_Common_FailedToStartMcpPlugin,
                                new DirectLocaleKey(mcpChatPlugin.Name)));
                    }
                }

                var functionContext = new ChatPluginFunctionContext(
                    assistant,
                    chatContext,
                    toolRulesets,
                    _serviceProvider,
                    cancellationToken);
                var actualFunctions = (await plugin.GetAvailableFunctionsAsync(functionContext))
                    .AsValueEnumerable()
                    .Where(function =>
                    {
                        var isFunctionAllowed = toolRulesets?.IsFunctionAllowed(plugin, function);
                        return isFunctionAllowed is true || (isFunctionAllowed is null && plugin.IsEnabled && function.IsEnabled);
                    })
                    .ToList();
                if (actualFunctions.Count > 0 || plugin is McpChatPlugin)
                {
                    resultPlugins.Add(new ChatPluginSnapshot(plugin, pluginNameDeduplicator, functionNameDeduplicator, actualFunctions));
                }
            }

            return new ChatPluginScope(resultPlugins);
        }
        finally
        {
            startingMcpActivity?.Dispose();
        }
    }

    private class ChatPluginScope(List<ChatPluginSnapshot> pluginSnapshots) : IChatPluginScope
    {
        public IReadOnlyList<ChatPlugin> Plugins => pluginSnapshots;

        public bool TryGetPluginAndFunction(
            string functionName,
            [NotNullWhen(true)] out ChatPlugin? plugin,
            [NotNullWhen(true)] out ChatFunction? function,
            [NotNullWhen(false)] out IReadOnlyList<string>? similarFunctionNames)
        {
            foreach (var pluginSnapshot in pluginSnapshots)
            {
                if (pluginSnapshot.TryGetChatFunction(functionName, out function))
                {
                    plugin = pluginSnapshot;
                    similarFunctionNames = null;
                    return true;
                }
            }

            plugin = null;
            function = null;
            similarFunctionNames = Process.ExtractTop(
                    functionName,
                    pluginSnapshots.SelectMany(p => p.GetChatFunctions()).Select(f => f.KernelFunction.Name),
                    limit: 5)
                .Where(r => r.Score >= 60)
                .Select(r => r.Value)
                .ToList();
            return false;
        }
    }

    internal void HandleClientDisposed(ManagedMcpClient client)
    {
        _managedClients.TryRemove(client.McpChatPlugin.Id, out _);
    }

    public void Dispose()
    {
        _runtimeManager.StatusChanged -= HandleRuntimeManagerStatusChanged;
        _builtInPluginsObserver.Dispose();
        _mcpPluginsObserver.Dispose();

        foreach (var mcpClient in _managedClients.Values)
        {
            mcpClient.DisposeAsync().Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        }

        _managedClients.Clear();
        _mcpPluginsSource.Clear();
        _disposables.Dispose();
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
        ToastResult? toastResult)
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
                .Create(LocaleResolver.Common_Info)
                .WithContent(LocaleResolver.RuntimeManager_InstallRuntime_Toast_Content.Format(dependency.DisplayName))
                .WithProgress(progress)
                .WithCancellationTokenSource(cancellationTokenSource)
                .OnBottomRight()
                .ShowInfo();

            await _runtimeManager.InstallAsync(dependency.Kind, progress, cancellationTokenSource.Token);
            RefreshMcpRuntimeWarnings();

            ToastManager.Success(LocaleResolver.RuntimeManager_InstallRuntime_SuccessToast_Title.Format(dependency.DisplayName));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to resolve runtime dependency {RuntimeKind} for MCP plugin {PluginId}.", dependency.Kind, mcpChatPlugin.Id);
            ToastManager.Error($"[{nameof(ChatPluginManager)}] Failed to resolve runtime dependency", e.GetFriendlyMessage());
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

        public void Dispose() => innerLoggerFactory.Dispose();

        private sealed class McpLogger(ILogger mcpChatPlugin, ILogger innerLogger) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => innerLogger.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                mcpChatPlugin.Log(logLevel, eventId, state, exception, formatter);
                innerLogger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }

    private class ChatPluginSnapshot : ChatPlugin
    {
        public override string Key => _originalChatPlugin.Key;
        public override IDynamicLocaleKey HeaderKey => _originalChatPlugin.HeaderKey;
        public override IDynamicLocaleKey DescriptionKey => _originalChatPlugin.DescriptionKey;
        public override LucideIconKind? Icon => _originalChatPlugin.Icon;
        public override string? BeautifulIcon => _originalChatPlugin.BeautifulIcon;
        public override int FunctionCount => _actualFunctions.Count;
        public override IReadOnlyBindableList<ChatFunction> Functions => throw new NotSupportedException();

        private readonly ChatPlugin _originalChatPlugin;
        private readonly List<ChatFunction> _actualFunctions;

        public ChatPluginSnapshot(
            ChatPlugin originalChatPlugin,
            NumberedDeduplicator pluginNameDeduplicator,
            NumberedDeduplicator functionNameDeduplicator,
            IReadOnlyList<ChatFunction> actualFunctions
        ) : base(pluginNameDeduplicator.Deduplicate(originalChatPlugin.Name))
        {
            _originalChatPlugin = originalChatPlugin;
            _actualFunctions = actualFunctions
                .AsValueEnumerable()
                .Select(EnsureUniqueFunctionName)
                .ToList();

            ChatFunction EnsureUniqueFunctionName(ChatFunction function)
            {
                var metadata = function.KernelFunction.Metadata;
                metadata.Name = functionNameDeduplicator.Deduplicate(metadata.Name);
                return function;
            }
        }

        public bool TryGetChatFunction(string name, [NotNullWhen(true)] out ChatFunction? function)
        {
            function = _actualFunctions.AsValueEnumerable().FirstOrDefault(f => f.KernelFunction.Metadata.Name == name);
            return function is not null;
        }

        public override bool TryGetFunction(string name, [NotNullWhen(true)] out KernelFunction? function)
        {
            function = _actualFunctions.AsValueEnumerable().Select(f => f.KernelFunction).FirstOrDefault(f => f.Metadata.Name == name);
            return function is not null;
        }

        public override IEnumerator<KernelFunction> GetEnumerator() => _actualFunctions.Select(f => f.KernelFunction).GetEnumerator();

        public override IReadOnlyList<ChatFunction> GetChatFunctions() => _actualFunctions;
    }
}