using System.ComponentModel;
using System.Text.Json;
using Everywhere.Chat;
using ModelContextProtocol.Server;

namespace Everywhere.Automation.WebView.Probe;

[McpServerToolType]
internal sealed class WebViewProbeTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly WebViewProbeSession _session;

    /// <summary>Records bounded native parent/child edges without the production identity map or Snapshotter.</summary>
    [McpServerTool(Name = "diagnose_topology", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Diagnose parent conflicts in the controlled WebView. Samples native Content View edges twice and saves topology.json; diagnostic IDs and pointers are not Agent target IDs.")]
    public Task<string> DiagnoseTopologyAsync(CancellationToken cancellationToken = default) => _session.DiagnoseTopologyAsync(cancellationToken);

    public WebViewProbeTools(WebViewProbeSession session) => _session = session;

    /// <summary>
    /// Navigates the controlled native WebView to an external web address.
    /// </summary>
    [McpServerTool(Name = "navigate", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Navigate the controlled native WebView to an absolute HTTP or HTTPS URL. The same process and visual target context remain alive across calls.")]
    public async Task<string> NavigateAsync(
        [Description("Absolute HTTP or HTTPS URL to load")] string address,
        [Description("Optional accessibility propagation delay after navigation; uses the server default when omitted")] int? settleMilliseconds = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await _session.NavigateAsync(address, settleMilliseconds, cancellationToken), JsonOptions);

    /// <summary>
    /// Reads one bounded region of the current live accessibility tree.
    /// </summary>
    [McpServerTool(Name = "query_visual", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Read a bounded live visual-tree region. Start with target 'root', then follow integer visual element IDs returned in the compact XML-like result. The tree may change between calls and status reports known incomplete results.")]
    public async Task<string> QueryVisualAsync(
        [Description("'root' for the current browser window, or an integer visual element ID returned by an earlier query")] string target = "root",
        [Description("Comma-separated directions: all, parent, child, previous, next, siblings, or none")] string directions = "all",
        [Description("1-based retained-member offset; pass root next back with the same target. Without observedMembers, use 1.")] int offset = 1,
        [Description("Optional maximum admitted nodes; values above 256 are clamped and the server default is used when omitted")] int? limit = null,
        [Description("Optional approximate prompt token budget; uses the server default when omitted")] int? targetTokenBudget = null,
        [Description("Start a persistent conversation turn, completing the previous one. Otherwise reuse it; without one, this call owns a temporary turn.")] bool shouldStartNewTurn = false,
        CancellationToken cancellationToken = default) =>
        (await _session.QueryAsync(target, directions, offset, limit, targetTokenBudget, cancellationToken, shouldStartNewTurn)).Content;

    /// <summary>
    /// Reads one bounded page from a retained visual element's current logical text stream.
    /// </summary>
    [McpServerTool(Name = "read_visual_text", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Read one bounded text page from an integer visual element ID returned by query_visual. Pass the returned next offset back unchanged. The live page may change between calls, and status reports known incomplete results.")]
    public async Task<string> ReadVisualTextAsync(
        [Description("Integer visual element ID returned by query_visual")] int target,
        [Description("Zero-based UTF-16 offset; pass the preceding result's next value, or zero for the first page")] int offset = 0,
        [Description("Approximate maximum UTF-16 code units requested; values above 16384 are clamped")] int limit = VisualQuery.DefaultTextLimit,
        [Description("Start a persistent conversation turn, completing the previous one. Otherwise reuse it; without one, this call owns a temporary turn.")] bool shouldStartNewTurn = false,
        CancellationToken cancellationToken = default) =>
        (await _session.ReadTextAsync(target, offset, limit, cancellationToken, shouldStartNewTurn)).Content;

    /// <summary>
    /// Reports the current controlled process and retained visual-target state.
    /// </summary>
    [McpServerTool(Name = "get_probe_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Report whether the controlled native WebView has started, its current address and roots, retained target/turn counts, and the artifact directory.")]
    public async Task<string> GetProbeStatusAsync(CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await _session.GetStatusAsync(cancellationToken), JsonOptions);
}
