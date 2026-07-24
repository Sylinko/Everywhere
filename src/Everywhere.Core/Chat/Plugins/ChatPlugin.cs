using System.Diagnostics.CodeAnalysis;
using System.Reactive.Linq;
using System.Text.Json.Serialization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat.Permissions;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Utilities;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat.Plugins;

[ObservableObject]
public abstract partial class ChatPlugin : KernelPlugin, IDisposable
{
    public abstract string Key { get; }

    [JsonIgnore]
    public abstract IDynamicLocaleKey HeaderKey { get; }

    [JsonIgnore]
    public abstract IDynamicLocaleKey DescriptionKey { get; }

    [JsonIgnore]
    public virtual LucideIconKind? Icon => null;

    /// <summary>
    /// Gets the uri or base64 data of the icon.
    /// </summary>
    [JsonIgnore]
    public virtual string? BeautifulIcon => null;

    public virtual bool IsDefaultEnabled => false;

    /// <summary>
    /// Gets the list of warnings for this plugin to be displayed in the UI.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyBindableList<ChatPluginWarning> Warnings { get; }

    /// <summary>
    /// Gets the list of functions provided by this plugin for Binding use in the UI.
    /// </summary>
    public abstract IReadOnlyBindableList<ChatFunction> Functions { get; }

    /// <summary>
    /// Gets the SettingsItems for this chat function.
    /// </summary>
    public virtual IReadOnlyList<SettingsItem>? SettingsItems => null;

    private readonly SourceCache<ChatPluginWarning, string> _warningsSource = new(x => x.Key);
    private readonly IDisposable _warningsConnection;

    protected ChatPlugin(string name) : base(name)
    {
        Warnings = _warningsSource.Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(out _warningsConnection);
    }

    /// <summary>
    /// Gets the list of functions provided by this plugin.
    /// </summary>
    /// <returns></returns>
    public abstract IReadOnlyList<ChatFunction> GetChatFunctions();

    public virtual ValueTask<IReadOnlyList<ChatFunction>> GetAvailableFunctionsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetChatFunctions());

    public virtual void Dispose()
    {
        _warningsConnection.Dispose();
        _warningsSource.Dispose();
        GC.SuppressFinalize(this);
    }

    public void SetWarning(string key, IDynamicLocaleKey? messageKey, ICommand? command = null)
    {
        if (messageKey is not null)
        {
            _warningsSource.AddOrUpdate(new ChatPluginWarning(key, messageKey, command));
        }
        else
        {
            RemoveWarning(key);
        }
    }

    public void RemoveWarning(string key) => _warningsSource.RemoveKey(key);
}

public sealed record ChatPluginWarning(string Key, IDynamicLocaleKey MessageKey, ICommand? Command = null)
{
    public IDynamicLocaleKey? ActionButtonContentKey =>
        Command is null ? null : new DynamicLocaleKey(LocaleKey.ChatPluginPage_WarningActionButton_Content);
}

public abstract class ChatPlugin<TChatFunction> : ChatPlugin where TChatFunction : ChatFunction
{
    public override IReadOnlyBindableList<ChatFunction> Functions { get; }

    public override int FunctionCount
    {
        get
        {
            var count = 0;
            _functionsSource.Edit(list => count = list.Count);
            return count;
        }
    }

    protected readonly SourceList<TChatFunction> _functionsSource = new();
    private readonly IDisposable _functionsConnection;

    protected ChatPlugin(string name) : base(name)
    {
        Functions = _functionsSource
            .Connect()
            .Cast(ChatFunction (x) => x)
            .Filter(x => x.IsVisible)
            .ObserveOnAvaloniaDispatcher()
            .BindEx(out _functionsConnection);
    }

    public override IReadOnlyList<ChatFunction> GetChatFunctions() => _functionsSource.Items;

    public override IEnumerator<KernelFunction> GetEnumerator() =>
        _functionsSource.Items.Select(f => f.KernelFunction).GetEnumerator();

    public override bool TryGetFunction(string name, [NotNullWhen(true)] out KernelFunction? function)
    {
        function = _functionsSource.Items
            .AsValueEnumerable()
            .Select(f => f.KernelFunction)
            .FirstOrDefault(f => f.Name == name);
        return function is not null;
    }

    public override void Dispose()
    {
        base.Dispose();

        _functionsSource.Dispose();
        _functionsConnection.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Chat kernel plugin implemented natively in Everywhere.
/// </summary>
/// <param name="name"></param>
public abstract class BuiltInChatPlugin(string name) : ChatPlugin<BuiltInChatFunction>(name)
{
    public override sealed string Key => $"builtin.{Name}";

    /// <summary>
    /// Indicates whether this plugin should be visible to users in the UI.
    /// Some plugins may be hidden but still enabled for internal use or by other plugins.
    /// </summary>
    public virtual bool IsVisible => true;

    public bool HasVisibleFunctions
    {
        get
        {
            var result = false;
            _functionsSource.Edit(list => result = list.AsValueEnumerable().Any(f => f.IsVisible));
            return result;
        }
    }
}

/// <summary>
/// Chat kernel plugin implemented with MCP.
/// </summary>
public sealed partial class McpChatPlugin : ChatPlugin<McpChatFunction>, ILogger
{
    /// <summary>
    /// Represents a log entry for the MCP plugin.
    /// </summary>
    /// <param name="Timestamp"></param>
    /// <param name="Level"></param>
    /// <param name="Message"></param>
    public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message)
    {
        public override string ToString()
        {
            return $"[{Level}] ({Timestamp:yyyy-MM-dd HH:mm:ss}) {Message}";
        }
    }

    /// <summary>
    /// Gets or sets the unique identifier of this MCP plugin.
    /// </summary>
    public Guid Id { get; set; }

    public override string Key => $"mcp.{Id}";

    public override DynamicLocaleKey HeaderKey => new DirectLocaleKey(TransportConfiguration?.Name ?? string.Empty);

    public override DynamicLocaleKey DescriptionKey => new DirectLocaleKey(TransportConfiguration?.Description ?? string.Empty);

    public override LucideIconKind? Icon => TransportConfiguration switch
    {
        StdioMcpTransportConfiguration => LucideIconKind.SquareTerminal,
        HttpMcpTransportConfiguration => LucideIconKind.Server,
        _ => null
    };

    public override string? BeautifulIcon => _beautifulIcon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderKey))]
    [NotifyPropertyChangedFor(nameof(DescriptionKey))]
    [NotifyPropertyChangedFor(nameof(Icon))]
    public partial McpTransportConfiguration? TransportConfiguration { get; set; }

    /// <summary>
    /// For MCP plugins, we cannot get the permission of each function. So we use a default permission for all functions.
    /// </summary>
    [ObservableProperty]
    public partial ChatFunctionPermissions DefaultPermissions { get; set; } = ChatFunctionPermissions.AllAccess;

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>
    /// Gets the log entries of this plugin.
    /// </summary>
    [DeepObserverIgnore]
    public IReadOnlyBindableList<LogEntry> LogEntries { get; }

    private const int MaxLogEntries = 1000;
    private const int PurgeThreshold = 200;

    private readonly SourceList<LogEntry> _logEntriesSource = new();
    private readonly IDisposable _logEntriesConnection;

    private string? _beautifulIcon;

    /// <summary>
    /// Chat kernel plugin implemented with MCP.
    /// </summary>
    /// <param name="mcpTransportConfiguration"></param>
    public McpChatPlugin(McpTransportConfiguration mcpTransportConfiguration) : this(Guid.CreateVersion7(), mcpTransportConfiguration) { }

    /// <summary>
    /// Chat kernel plugin implemented with MCP.
    /// </summary>
    /// <param name="id">use GUID to avoid name conflicts</param>
    /// <param name="mcpTransportConfiguration"></param>
    public McpChatPlugin(Guid id, McpTransportConfiguration mcpTransportConfiguration) : base(id.ToString("N"))
    {
        Id = id;
        TransportConfiguration = mcpTransportConfiguration;

        LogEntries = _logEntriesSource
            .Connect()
            .Buffer(TimeSpan.FromMilliseconds(250))
            .FlattenBufferResult()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(out _logEntriesConnection);
    }

    public void EditFunctions(Action<IExtendedList<McpChatFunction>> updateAction)
    {
        _functionsSource.Edit(updateAction);
    }

    /// <summary>
    /// Updates the BeautifulIcon of this plugin and raises the PropertyChanged event.
    /// </summary>
    /// <param name="imageSource"></param>
    public void UpdateBeautifulIcon(string? imageSource)
    {
        _beautifulIcon = imageSource;
        OnPropertyChanged(nameof(BeautifulIcon));
    }

    void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        _logEntriesSource.Edit(list =>
        {
            list.Add(new LogEntry(DateTime.Now, logLevel, message));
            if (list.Count > MaxLogEntries + PurgeThreshold)
            {
                list.RemoveRange(0, list.Count - MaxLogEntries);
            }
        });
    }

    bool ILogger.IsEnabled(LogLevel logLevel) => true;

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public override void Dispose()
    {
        base.Dispose();

        _logEntriesConnection.Dispose();
        _logEntriesSource.Dispose();
    }
}
