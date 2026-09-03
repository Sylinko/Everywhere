using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Everywhere.Automation.CefSharp.Probe;

[McpServerToolType]
internal sealed class CefSharpProbeTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly CefSharpProbeSession _session;

    public CefSharpProbeTools(CefSharpProbeSession session) => _session = session;

    /// <summary>
    /// Navigates the controlled CefSharp process to an external web address.
    /// </summary>
    [McpServerTool(Name = "navigate", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Navigate the controlled CefSharp browser to an absolute HTTP or HTTPS URL. The same process and visual target context remain alive across calls.")]
    public async Task<string> NavigateAsync(
        [Description("Absolute HTTP or HTTPS URL to load")] string address,
        [Description("Optional accessibility propagation delay after navigation; uses the server default when omitted")] int? settleMilliseconds = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await _session.NavigateAsync(address, settleMilliseconds, cancellationToken), JsonOptions);

    /// <summary>
    /// Reads one bounded region of the current live accessibility tree.
    /// </summary>
    [McpServerTool(Name = "query_visual", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Read a bounded live visual-tree region. Start with target 'root', then follow decimal IDs returned in the compact XML-like result with narrower directions or Composite offsets. The tree may change between calls and status reports known incomplete results.")]
    public async Task<string> QueryVisualAsync(
        [Description("'root' for the current browser window, or a decimal Element/Composite ID returned by an earlier query")] string target = "root",
        [Description("Comma-separated directions: all, parent, child, previous, next, siblings, or none")] string directions = "all",
        [Description("1-based Composite member offset; Element targets support only 1")] int offset = 1,
        [Description("Optional maximum admitted nodes; values above 256 are clamped and the server default is used when omitted")] int? limit = null,
        [Description("Optional approximate prompt token budget; uses the server default when omitted")] int? targetTokenBudget = null,
        CancellationToken cancellationToken = default) =>
        (await _session.QueryAsync(target, directions, offset, limit, targetTokenBudget, cancellationToken)).Content;

    /// <summary>
    /// Reports the current controlled process and retained visual-target state.
    /// </summary>
    [McpServerTool(Name = "get_probe_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Report whether the controlled CefSharp process has started, its current address and roots, retained target/turn counts, and the artifact directory.")]
    public async Task<string> GetProbeStatusAsync(CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await _session.GetStatusAsync(cancellationToken), JsonOptions);
}
