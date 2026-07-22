using Everywhere.Chat.Plugins;
using Everywhere.Collections;

namespace Everywhere.Configuration;

public sealed class PluginSettings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider)
{
    /// <summary>
    /// Gets or sets exact plugin and function enablement overrides.
    /// </summary>
    /// <remarks>
    /// Missing entries inherit the default declared by the tool definition.
    /// </remarks>
    public ToolEnablementSettings ToolEnablement { get; set; } = new();

    /// <summary>
    /// Gets or sets exact automatic-approval overrides for plugin functions.
    /// </summary>
    public ToolAutoApprovalSettings ToolAutoApproval { get; set; } = new();

    /// <summary>
    /// Gets the MCP transport configurations keyed by plugin id.
    /// </summary>
    public ObservableDictionary<Guid, McpTransportConfiguration> McpChatPlugins { get; } = new();

    /// <summary>
    /// Gets or sets the web search engine settings.
    /// </summary>
    public WebSearchEngineSettings WebSearchEngine { get; set; } = new();

    /// <summary>
    /// Gets or sets the web browser settings.
    /// </summary>
    public WebBrowserSettings WebBrowser { get; set; } = new();

    /// <summary>
    /// Gets or sets the terminal settings.
    /// </summary>
    public TerminalPluginSettings Terminal { get; set; } = new();
}