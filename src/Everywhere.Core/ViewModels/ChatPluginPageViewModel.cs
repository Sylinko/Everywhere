using System.Text.Json;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat.Plugins;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Views;
using ModelContextProtocol.Client;
using ShadUI;

namespace Everywhere.ViewModels;

public partial class ChatPluginPageViewModel : BusyViewModelBase
{
    public ToolSettingsPresentation ToolSettings { get; }

    public ToolPluginPresentation? SelectedPlugin
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            OnPropertyChanged(nameof(SelectedBuiltInPlugin));
            OnPropertyChanged(nameof(SelectedMcpPluginPresentation));
            OnPropertyChanged(nameof(SelectedMcpPlugin));

            _contentTabItems.Clear();
            if (value is null) return;

            _contentTabItems.Add(new FunctionsTabItem(value));

            if (value.Plugin.SettingsItems is { Count: > 0 } settingsItems)
            {
                _contentTabItems.Add(new SettingsTabItem(settingsItems));
            }

            if (value.Plugin is McpChatPlugin mcpPlugin)
            {
                _contentTabItems.Add(new LogsTabItem(mcpPlugin.LogEntries));
            }
        }
    }

    /// <summary>
    /// Helper property to get the selected plugin as a BuiltInChatPlugin.
    /// </summary>
    public ToolPluginPresentation? SelectedBuiltInPlugin
    {
        get => SelectedPlugin?.Plugin is BuiltInChatPlugin ? SelectedPlugin : null;
        set
        {
            if (value is not null)
            {
                SelectedPlugin = value;
            }
        }
    }

    /// <summary>
    /// Helper property to get the selected plugin as a McpChatPlugin.
    /// </summary>
    public ToolPluginPresentation? SelectedMcpPluginPresentation
    {
        get => SelectedPlugin?.Plugin is McpChatPlugin ? SelectedPlugin : null;
        set
        {
            if (value is not null)
            {
                SelectedPlugin = value;
            }
        }
    }

    public McpChatPlugin? SelectedMcpPlugin => SelectedMcpPluginPresentation?.Plugin as McpChatPlugin;

    public IReadOnlyBindableList<IContentTabItem> ContentTabItems => _contentTabItems;

    private readonly BindableList<IContentTabItem> _contentTabItems = [];

    private readonly IChatPluginManager _manager;

    public ChatPluginPageViewModel(IChatPluginManager manager, Settings settings)
    {
        _manager = manager;
        ToolSettings = new ToolSettingsPresentation(
            manager,
            ToolSettingsContext.CreateGlobal(settings.Plugin.ToolEnablementRulesets, settings.Plugin.ToolBypassApprovalRulesets));
        LifetimeDisposables.Add(ToolSettings);
    }

    [RelayCommand]
    private async Task AddMcpPluginAsync(CancellationToken cancellationToken)
    {
        var form = new McpTransportConfigurationForm();
        var result = await DialogHost
            .CreateDialog(form, LocaleResolver.ChatPluginPageViewModel_AddMcpPlugin_DialogTitle)
            .WithPrimaryButton(
                LocaleResolver.Common_OK,
                (_, e) => e.Cancel = !form.Configuration.Validate())
            .WithCancelButton(LocaleResolver.Common_Cancel)
            .ShowAsync(cancellationToken);
        if (result != DialogResult.Primary) return;
        if (form.Configuration.HasErrors) return;

        try
        {
            var plugin = _manager.CreateMcpPlugin(form.Configuration);
            SelectedPlugin = ToolSettings.Plugins.FirstOrDefault(item => ReferenceEquals(item.Plugin, plugin));
        }
        catch (Exception e)
        {
            ToastExceptionHandler.HandleException(e, "Failed to add MCP Plugin");
            return;
        }

        _manager.RefreshMcpRuntimeWarnings();
    }

    [RelayCommand]
    private async Task ImportMcpPluginAsync()
    {
        var form = new McpImportForm();
        var result = await DialogHost
            .CreateDialog(form, LocaleResolver.ChatPluginPageViewModel_ImportMcpPlugin_DialogTitle)
            .WithPrimaryButton(LocaleResolver.Common_OK)
            .WithCancelButton(LocaleResolver.Common_Cancel)
            .ShowAsync();
        if (result != DialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(form.McpJson)) return;

        try
        {
            var configurations = ParseMcpConfigurations(form.McpJson);
            var count = 0;
            foreach (var configuration in configurations.AsValueEnumerable().Where(c => c.Validate()))
            {
                _manager.CreateMcpPlugin(configuration);
                count++;
            }

            if (count == 0)
            {
                ToastHost
                    .CreateToast(LocaleResolver.ChatPluginPageViewModel_ImportMcpPlugin_NotFoundToast_Title)
                    .OnBottomRight()
                    .ShowWarning();
            }
            else if (count < configurations.Count)
            {
                ToastHost
                    .CreateToast(
                        LocaleResolver.ChatPluginPageViewModel_ImportMcpPlugin_PartialSuccessToast_Title.Format(count, configurations.Count - count))
                    .OnBottomRight()
                    .ShowWarning();
            }
            else
            {
                ToastHost
                    .CreateToast(LocaleResolver.ChatPluginPageViewModel_ImportMcpPlugin_SuccessToast_Title.Format(count))
                    .OnBottomRight()
                    .ShowSuccess();
            }
        }
        catch (Exception e)
        {
            ToastExceptionHandler.HandleException(e, LocaleResolver.ChatPluginPageViewModel_ImportMcpPlugin_FailedToast_Title);
        }
    }

    private static List<McpTransportConfiguration> ParseMcpConfigurations(string json)
    {
        var configurations = new List<McpTransportConfiguration>();
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var document = JsonDocument.Parse(json, options);
        var root = document.RootElement;

        // Case A: Standard Format { "mcpServers": { "name": { ... } } }
        // Case B: Root node as dictionary { "server1": { ... }, "server2": { ... } }
        // Case C: Root node as array [ { "name": "...", "command": "..." }, ... ]
        // Case D: Root node as single configuration object { "command": "...", ... }
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
            {
                // Case C
                configurations.AddRange(
                    root.EnumerateArray()
                        .Select(e => ParseSingleMcpConfiguration(e, null))
                        .OfType<McpTransportConfiguration>());
                break;
            }
            case JsonValueKind.Object when root.TryGetProperty("mcpServers", out var mcpServers) && mcpServers.ValueKind == JsonValueKind.Object:
            {
                // Case A
                configurations.AddRange(
                    mcpServers.EnumerateObject()
                        .Select(p => ParseSingleMcpConfiguration(p.Value, p.Name))
                        .OfType<McpTransportConfiguration>());
                break;
            }
            case JsonValueKind.Object when IsMcpConfiguration(root):
            {
                // Case D
                var configuration = ParseSingleMcpConfiguration(root, null);
                if (configuration is not null) configurations.Add(configuration);
                break;
            }
            case JsonValueKind.Object:
            {
                // Case B
                configurations.AddRange(
                    root.EnumerateObject()
                        .Select(p => ParseSingleMcpConfiguration(p.Value, p.Name))
                        .OfType<McpTransportConfiguration>());
                break;
            }
        }

        return configurations;
    }

    private static bool IsMcpConfiguration(JsonElement element) =>
        element.TryGetProperty("command", out _) ||
        element.TryGetProperty("url", out _) ||
        element.TryGetProperty("type", out var type) && (type.GetString() == "sse" || type.GetString() == "stdio");

    private static McpTransportConfiguration? ParseSingleMcpConfiguration(JsonElement element, string? name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        if (element.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
        {
            name = nameProp.GetString();
        }

        // Determine type
        HttpTransportMode? httpTransportMode = null;
        if (element.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
        {
            var typePropString = typeProp.GetString();
            if (typePropString?.Equals("sse", StringComparison.OrdinalIgnoreCase) is true)
            {
                httpTransportMode = HttpTransportMode.Sse;
            }
            else if (typePropString?.EndsWith("http", StringComparison.OrdinalIgnoreCase) is true)
            {
                httpTransportMode = HttpTransportMode.StreamableHttp;
            }
        }
        else if (element.TryGetProperty("url", out _))
        {
            httpTransportMode = HttpTransportMode.AutoDetect;
        }

        McpTransportConfiguration configuration;
        if (httpTransportMode.HasValue)
        {
            var httpConfiguration = new HttpMcpTransportConfiguration
            {
                TransportMode = httpTransportMode.Value
            };
            if (element.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
            {
                httpConfiguration.Endpoint = urlProp.GetString() ?? string.Empty;
            }
            if (element.TryGetProperty("headers", out var headersProp) && headersProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var header in headersProp.EnumerateObject()
                             .AsValueEnumerable()
                             .Where(header => header.Value.ValueKind == JsonValueKind.String))
                {
                    httpConfiguration.Headers.Add(new ObservableKeyValuePair<string, string>(header.Name, header.Value.GetString() ?? string.Empty));
                }
            }
            configuration = httpConfiguration;
        }
        else
        {
            // Assume Stdio
            var stdioConfiguration = new StdioMcpTransportConfiguration();
            if (element.TryGetProperty("command", out var cmdProp) && cmdProp.ValueKind == JsonValueKind.String)
            {
                stdioConfiguration.Command = cmdProp.GetString() ?? string.Empty;
            }

            if ((element.TryGetProperty("args", out var argsProp) || element.TryGetProperty("arguments", out argsProp)) &&
                argsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var arg in argsProp.EnumerateArray()
                             .AsValueEnumerable()
                             .Where(arg => arg.ValueKind == JsonValueKind.String))
                {
                    stdioConfiguration.Arguments.Add(new BindingWrapper<string>(arg.GetString() ?? string.Empty));
                }
            }

            if ((element.TryGetProperty("env", out var envProp) || element.TryGetProperty("environmentVariables", out envProp)) &&
                envProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var env in envProp.EnumerateObject()
                             .AsValueEnumerable()
                             .Where(env => env.Value.ValueKind == JsonValueKind.String))
                {
                    stdioConfiguration.EnvironmentVariables.Add(new ObservableKeyValuePair<string, string?>(env.Name, env.Value.GetString()));
                }
            }
            configuration = stdioConfiguration;
        }

        configuration.Name = name ?? LocaleResolver.McpTransportConfiguration_DefaultName;
        return configuration;
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task StartMcpPluginAsync(McpChatPlugin? plugin, CancellationToken cancellationToken)
    {
        if (plugin is null) return Task.CompletedTask;

        return ExecuteBusyTaskAsync(
            token => Task.Run(() => _manager.StartMcpClientAsync(plugin, token), token),
            ToastExceptionHandler,
            cancellationToken: cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task StopMcpPluginAsync(McpChatPlugin? plugin, CancellationToken cancellationToken)
    {
        if (plugin is null) return Task.CompletedTask;

        return ExecuteBusyTaskAsync(
            token => Task.Run(() => _manager.StopMcpClientAsync(plugin), token),
            ToastExceptionHandler,
            cancellationToken: cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task EditMcpPluginAsync(McpChatPlugin? plugin, CancellationToken cancellationToken)
    {
        if (plugin is null) return Task.CompletedTask;

        return ExecuteBusyTaskAsync(
            async token =>
            {
                var form = new McpTransportConfigurationForm
                {
                    Configuration = JsonSerializer.Deserialize(
                        JsonSerializer.Serialize(
                            plugin.TransportConfiguration,
                            McpTransportConfigurationJsonSerializerContext.Default.McpTransportConfiguration),
                        McpTransportConfigurationJsonSerializerContext.Default.McpTransportConfiguration) ?? new StdioMcpTransportConfiguration()
                };
                var result = await DialogHost
                    .CreateDialog(form, LocaleResolver.ChatPluginPageViewModel_EditMcpPlugin_DialogTitle)
                    .WithPrimaryButton(
                        LocaleResolver.Common_OK,
                        (_, e) => e.Cancel = !form.Configuration.Validate())
                    .WithCancelButton(LocaleResolver.Common_Cancel)
                    .ShowAsync(token);
                if (result != DialogResult.Primary) return;
                if (form.Configuration.HasErrors) return;

                await Task.Run(() => _manager.UpdateMcpPluginAsync(plugin, form.Configuration), token);
            },
            ToastExceptionHandler,
            cancellationToken: cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task RemoveMcpPluginAsync(McpChatPlugin? plugin, CancellationToken cancellationToken)
    {
        if (plugin is null) return Task.CompletedTask;

        return ExecuteBusyTaskAsync(
            async token =>
            {
                var result = await DialogHost
                    .CreateDialog(LocaleResolver.ChatPluginPageViewModel_RemoveMcpPlugin_ConfirmationMessage.Format(plugin.HeaderKey))
                    .WithPrimaryButton(LocaleResolver.Common_Yes)
                    .WithCancelButton(LocaleResolver.Common_No)
                    .ShowAsync(token);
                if (result != DialogResult.Primary) return;

                await Task.Run(() => _manager.RemoveMcpPluginAsync(plugin), token);

                if (ReferenceEquals(SelectedPlugin?.Plugin, plugin)) SelectedPlugin = null;
            },
            ToastExceptionHandler,
            cancellationToken: cancellationToken);
    }

    [RelayCommand]
    private async Task CopyLogsAsync(IReadOnlyBindableList<McpChatPlugin.LogEntry>? logEntries)
    {
        if (logEntries is not { Count: > 0 }) return;

        await App.Clipboard.SetTextAsync(string.Join('\n', logEntries));
        ToastHost
            .CreateToast("Logs copied to clipboard.")
            .OnBottomRight()
            .ShowSuccess();
    }

    protected override void OnIsBusyChanged()
    {
        base.OnIsBusyChanged();
        StartMcpPluginCommand.NotifyCanExecuteChanged();
        StopMcpPluginCommand.NotifyCanExecuteChanged();
        EditMcpPluginCommand.NotifyCanExecuteChanged();
        RemoveMcpPluginCommand.NotifyCanExecuteChanged();
    }

    #region ContentTabItems

    // Helpers for content tab items in MVVM pattern

    public interface IContentTabItem
    {
        IDynamicLocaleKey Header { get; }
    }

    public class SettingsTabItem(IReadOnlyList<SettingsItem> settingsItems) : IContentTabItem
    {
        public IDynamicLocaleKey Header => new DynamicLocaleKey(LocaleKey.ChatPluginPage_TabItem_Settings_Header);

        public IReadOnlyList<SettingsItem> SettingsItems { get; } = settingsItems;
    }

    public class FunctionsTabItem(ToolPluginPresentation plugin) : IContentTabItem
    {
        public IDynamicLocaleKey Header => new DynamicLocaleKey(LocaleKey.ChatPluginPage_TabItem_Functions_Header);

        public ToolPluginPresentation Plugin { get; } = plugin;
    }

    public partial class LogsTabItem(IReadOnlyBindableList<McpChatPlugin.LogEntry> logEntries) : ObservableObject, IContentTabItem
    {
        public IDynamicLocaleKey Header => new DynamicLocaleKey(LocaleKey.ChatPluginPage_TabItem_Logs_Header);

        public IReadOnlyBindableList<McpChatPlugin.LogEntry> LogEntries { get; } = logEntries;

        [ObservableProperty]
        public partial bool ShowTimestamp { get; set; }
    }

    #endregion
}
