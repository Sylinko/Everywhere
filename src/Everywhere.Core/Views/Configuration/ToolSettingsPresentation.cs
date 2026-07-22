using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat.Plugins;
using Everywhere.Collections;

namespace Everywhere.Views;

/// <summary>
/// Resolves and edits tool settings for either the global settings context or one assistant.
/// </summary>
public sealed class ToolSettingsContext : IDisposable
{
    public bool IsFollowingGlobal => !_isGlobal && _overrides is null;

    public bool CanEdit => _isGlobal || _overrides is not null;

    public bool CanEditAutoApproval => _autoApproval is not null;

    public event EventHandler? Changed;

    private readonly ToolEnablementSettings _global;
    private readonly ToolAutoApprovalSettings? _autoApproval;
    private readonly bool _isGlobal;
    private ToolEnablementSettings? _overrides;

    public ToolSettingsContext(ToolEnablementSettings global, ToolAutoApprovalSettings autoApproval)
    {
        _global = global;
        _overrides = global;
        _autoApproval = autoApproval;
        _isGlobal = true;
        Subscribe(global);
        _autoApproval.CollectionChanged += HandleCollectionChanged;
    }

    public ToolSettingsContext(ToolEnablementSettings global, ToolEnablementSettings? assistantOverrides)
    {
        _global = global;
        _overrides = assistantOverrides;
        Subscribe(global);
        Subscribe(assistantOverrides);
    }

    public void SetAssistantOverrides(ToolEnablementSettings? value)
    {
        if (_isGlobal || ReferenceEquals(_overrides, value)) return;

        Unsubscribe(_overrides);
        _overrides = value;
        Subscribe(_overrides);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool GetPluginEnabled(ChatPlugin plugin)
    {
        if (!_isGlobal && _overrides?.IsPluginAllowed(plugin) is { } assistantValue) return assistantValue;
        return _global.IsPluginAllowed(plugin) ?? plugin.IsDefaultEnabled;
    }

    public bool GetFunctionEnabled(ChatPlugin plugin, ChatFunction function)
    {
        if (!_isGlobal && _overrides?.IsFunctionAllowed(plugin, function) is { } assistantValue) return assistantValue;
        return _global.IsFunctionAllowed(plugin, function) ?? function.IsDefaultEnabled;
    }

    public bool GetAutoApproval(ChatPlugin plugin, ChatFunction function)
    {
        if (_autoApproval is null || !function.IsAutoApproveAllowed) return false;
        var key = ToolSettingsKey.ForFunction(plugin, function);
        return _autoApproval.TryGetValue(key, out var value) ? value : function.IsDefaultAutoApprove;
    }

    /// <summary>
    /// Sets only the plugin-level override. Function overrides remain unchanged.
    /// </summary>
    public void SetPluginEnabled(ChatPlugin plugin, bool value)
    {
        if (!CanEdit) return;

        SetPluginOverride(plugin, value);
    }

    /// <summary>
    /// Sets only the function-level override. The plugin-level override remains unchanged.
    /// </summary>
    public void SetFunctionEnabled(ChatPlugin plugin, ChatFunction function, bool value)
    {
        if (!CanEdit) return;

        SetFunctionOverride(plugin, function, value);
    }

    public void SetAutoApproval(ChatPlugin plugin, ChatFunction function, bool value)
    {
        if (_autoApproval is null || !function.IsAutoApproveAllowed) return;

        var key = ToolSettingsKey.ForFunction(plugin, function);
        if (value == function.IsDefaultAutoApprove)
        {
            _autoApproval.Remove(key);
        }
        else
        {
            _autoApproval[key] = value;
        }
    }

    public void Dispose()
    {
        Unsubscribe(_global);
        if (!_isGlobal) Unsubscribe(_overrides);
        if (_autoApproval is not null) _autoApproval.CollectionChanged -= HandleCollectionChanged;
    }

    private void SetPluginOverride(ChatPlugin plugin, bool value)
    {
        var records = _overrides;
        if (records is null) return;

        var baseline = _isGlobal ? plugin.IsDefaultEnabled : _global.IsPluginAllowed(plugin) ?? plugin.IsDefaultEnabled;
        SetOverride(records, ToolSettingsKey.ForPlugin(plugin), value, baseline);
    }

    private void SetFunctionOverride(ChatPlugin plugin, ChatFunction function, bool value)
    {
        var records = _overrides;
        if (records is null) return;

        var baseline = _isGlobal ? function.IsDefaultEnabled : _global.IsFunctionAllowed(plugin, function) ?? function.IsDefaultEnabled;
        SetOverride(records, ToolSettingsKey.ForFunction(plugin, function), value, baseline);
    }

    private static void SetOverride(ToolEnablementSettings records, string key, bool value, bool baseline)
    {
        if (value == baseline)
        {
            records.Remove(key);
        }
        else
        {
            records[key] = value;
        }
    }

    private void Subscribe(ToolEnablementSettings? settings)
    {
        if (settings is not null) settings.CollectionChanged += HandleCollectionChanged;
    }

    private void Unsubscribe(ToolEnablementSettings? settings)
    {
        if (settings is not null) settings.CollectionChanged -= HandleCollectionChanged;
    }

    private void HandleCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Incrementally projects the plugin catalog into stable settings rows.
/// </summary>
public sealed class ToolSettingsPresentation : IDisposable
{
    public IReadOnlyBindableList<ToolPluginPresentation> Plugins { get; }

    public IReadOnlyBindableList<ToolPluginPresentation> BuiltInPlugins { get; }

    public IReadOnlyBindableList<ToolPluginPresentation> McpPlugins { get; }

    public ToolSettingsContext Context { get; }

    private readonly CompositeDisposable _disposables = new();

    public ToolSettingsPresentation(IChatPluginManager manager, ToolSettingsContext context)
    {
        Context = context;
        Plugins = manager.BuiltInPlugins
            .ToObservableChangeSet<IReadOnlyBindableList<BuiltInChatPlugin>, BuiltInChatPlugin>()
            .Transform(ChatPlugin (plugin) => plugin)
            .Or(manager.McpPlugins
                .ToObservableChangeSet<IReadOnlyBindableList<McpChatPlugin>, McpChatPlugin>()
                .Transform(ChatPlugin (plugin) => plugin))
            .Transform(plugin => new ToolPluginPresentation(plugin, context))
            .DisposeMany()
            .BindEx(_disposables);

        BuiltInPlugins = Plugins
            .ToObservableChangeSet<IReadOnlyBindableList<ToolPluginPresentation>, ToolPluginPresentation>()
            .Filter(static presentation => presentation.Plugin is BuiltInChatPlugin)
            .BindEx(_disposables);
        McpPlugins = Plugins
            .ToObservableChangeSet<IReadOnlyBindableList<ToolPluginPresentation>, ToolPluginPresentation>()
            .Filter(static presentation => presentation.Plugin is McpChatPlugin)
            .BindEx(_disposables);
    }

    public void Dispose()
    {
        _disposables.Dispose();
        Context.Dispose();
    }
}

public sealed class ToolPluginPresentation : ObservableObject, IDisposable
{
    public ChatPlugin Plugin { get; }

    public IReadOnlyBindableList<ToolFunctionPresentation> Functions { get; }

    public bool IsEnabled
    {
        get => _context.GetPluginEnabled(Plugin);
        set => _context.SetPluginEnabled(Plugin, value);
    }

    public bool CanEdit => _context.CanEdit;

    public int EnabledFunctionCount => Functions.Count(static function => function.IsEnabled);

    public bool IsRunning => Plugin is McpChatPlugin { IsRunning: true };

    private readonly ToolSettingsContext _context;
    private readonly IDisposable _functionsSubscription;

    public ToolPluginPresentation(ChatPlugin plugin, ToolSettingsContext context)
    {
        Plugin = plugin;
        _context = context;
        Functions = plugin.Functions
            .ToObservableChangeSet<IReadOnlyBindableList<ChatFunction>, ChatFunction>()
            .Transform(function => new ToolFunctionPresentation(this, function, context))
            .BindEx(out _functionsSubscription);
        _context.Changed += HandleSettingsChanged;
        Plugin.PropertyChanged += HandlePluginPropertyChanged;
    }

    public void Dispose()
    {
        _context.Changed -= HandleSettingsChanged;
        Plugin.PropertyChanged -= HandlePluginPropertyChanged;
        _functionsSubscription.Dispose();
    }

    private void HandleSettingsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(EnabledFunctionCount));
        foreach (var function in Functions) function.Refresh();
    }

    private void HandlePluginPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(McpChatPlugin.IsRunning)) OnPropertyChanged(nameof(IsRunning));
    }
}

public sealed class ToolFunctionPresentation(ToolPluginPresentation plugin, ChatFunction function, ToolSettingsContext context) : ObservableObject
{
    public ChatFunction Function { get; } = function;

    public bool IsEnabled
    {
        get => context.GetFunctionEnabled(plugin.Plugin, Function);
        set => context.SetFunctionEnabled(plugin.Plugin, Function, value);
    }

    public bool CanEdit => context.CanEdit;

    public bool AutoApproval
    {
        get => context.GetAutoApproval(plugin.Plugin, Function);
        set => context.SetAutoApproval(plugin.Plugin, Function, value);
    }

    public bool CanEditAutoApproval => context.CanEditAutoApproval && Function.IsAutoApproveAllowed;

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(AutoApproval));
        OnPropertyChanged(nameof(CanEditAutoApproval));
    }
}