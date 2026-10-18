using Everywhere.Chat.Plugins;
using Everywhere.Collections;
using Everywhere.Chat.Permissions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Configuration;

public sealed partial class PluginSettings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider)
{
    /// <summary>
    /// Gets or sets the approval mode captured by newly started turns.
    /// </summary>
    [ObservableProperty]
    public partial ToolApprovalMode ApprovalMode { get; set; }

    /// <summary>
    /// Gets or sets exact plugin and function enablement overrides.
    /// </summary>
    /// <remarks>
    /// Missing entries inherit the default declared by the tool definition.
    /// </remarks>
    public ObservableToolRulesets ToolEnablementRulesets { get; set; } = new();

    /// <summary>
    /// Gets or sets exact rules that allow plugin functions to bypass approval.
    /// </summary>
    /// <remarks>
    /// A false value requires approval and does not deny execution.
    /// </remarks>
    public ObservableToolRulesets ToolBypassApprovalRulesets { get; set; } = new();

    /// <summary>
    /// Gets the MCP transport configurations keyed by plugin id.
    /// </summary>
    public ObservableDictionary<Guid, McpTransportConfiguration> McpChatPlugins { get; } = new();

    /// <summary>
    /// Gets the web search engine settings.
    /// </summary>
    public WebSearchEngineSettings WebSearchEngine { get; } = new(serviceProvider);

    /// <summary>
    /// Gets the file-system path approval settings.
    /// </summary>
    public FileSystemSettings FileSystem { get; } = new();

    /// <summary>
    /// Gets the web browser settings.
    /// </summary>
    public WebBrowserSettings WebBrowser { get; } = new(serviceProvider);

    /// <summary>
    /// Gets the terminal settings.
    /// </summary>
    public TerminalPluginSettings Terminal { get; } = new();
}