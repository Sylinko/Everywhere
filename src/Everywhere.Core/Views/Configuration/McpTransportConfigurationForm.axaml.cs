using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using Everywhere.Chat.Plugins;

namespace Everywhere.Views;

public class McpTransportConfigurationForm : TemplatedControl
{
    public static readonly DirectProperty<McpTransportConfigurationForm, McpTransportConfiguration> ConfigurationProperty =
        AvaloniaProperty.RegisterDirect<McpTransportConfigurationForm, McpTransportConfiguration>(
        nameof(Configuration),
        o => o.Configuration,
        (o, v) => o.Configuration = v);

    public static readonly DirectProperty<McpTransportConfigurationForm, int> SelectedTabIndexProperty =
        AvaloniaProperty.RegisterDirect<McpTransportConfigurationForm, int>(
            nameof(SelectedTabIndex),
            o => o.SelectedTabIndex,
            (o, v) => o.SelectedTabIndex = v);

    public static readonly DirectProperty<McpTransportConfigurationForm, StdioMcpTransportConfiguration> StdioConfigurationProperty =
        AvaloniaProperty.RegisterDirect<McpTransportConfigurationForm, StdioMcpTransportConfiguration>(
            nameof(StdioConfiguration),
            o => o.StdioConfiguration);

    public static readonly DirectProperty<McpTransportConfigurationForm, HttpMcpTransportConfiguration> HttpConfigurationProperty =
        AvaloniaProperty.RegisterDirect<McpTransportConfigurationForm, HttpMcpTransportConfiguration>(
            nameof(HttpConfiguration),
            o => o.HttpConfiguration);

    public McpTransportConfiguration Configuration
    {
        get => _configuration;
        set
        {
            if (!SetAndRaise(ConfigurationProperty, ref _configuration, value)) return;

            switch (value)
            {
                case StdioMcpTransportConfiguration stdioConfig:
                {
                    var oldConfig = StdioConfiguration;
                    StdioConfiguration = stdioConfig;
                    SelectedTabIndex = 0;
                    RaisePropertyChanged(StdioConfigurationProperty, oldConfig, StdioConfiguration);
                    break;
                }
                case HttpMcpTransportConfiguration sseConfig:
                {
                    var oldConfig = HttpConfiguration;
                    HttpConfiguration = sseConfig;
                    SelectedTabIndex = 1;
                    RaisePropertyChanged(HttpConfigurationProperty, oldConfig, HttpConfiguration);
                    break;
                }
            }
        }
    }
    public int SelectedTabIndex
    {
        get => _configuration == StdioConfiguration ? 0 : 1;
        set
        {
            var oldValue = _configuration;
            _configuration = value == 0 ? StdioConfiguration : HttpConfiguration;
            _configuration.Name = oldValue.Name;
            _configuration.Description = oldValue.Description;
            RaisePropertyChanged(ConfigurationProperty, oldValue, HttpConfiguration);
        }
    }

    public StdioMcpTransportConfiguration StdioConfiguration { get; private set; }

    public HttpMcpTransportConfiguration HttpConfiguration { get; private set; }

    private McpTransportConfiguration _configuration;

    public McpTransportConfigurationForm()
    {
        _configuration = StdioConfiguration = new StdioMcpTransportConfiguration();
        HttpConfiguration = new HttpMcpTransportConfiguration();
    }

    public async void BrowseWorkingDirectory()
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
        StdioConfiguration.WorkingDirectory = result.FirstOrDefault()?.Path.LocalPath;
    }
}