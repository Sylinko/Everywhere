using Everywhere.I18N;

namespace Everywhere.StrategyEngine;

public sealed record StrategyContextRequirements
{
    public bool NeedsClipboard { get; init; }

    public bool NeedsVisualTree { get; init; }

    public IReadOnlySet<string> ExtraRoots { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> AssistantPaths { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public sealed record ExtraContextRequest
{
    public required string PublicRoot { get; init; }

    public IReadOnlyList<string> RequiredPaths { get; init; } = [];

    public required TimeSpan Timeout { get; init; }
}

public interface IExtraContextProvider
{
    string Id { get; }

    string PublicRoot { get; }

    IDynamicResourceKey PermissionDescriptionKey { get; }

    bool CanCollect(StrategyContext baseContext, ExtraContextRequest request);

    Task<ExtraContextNode?> CollectAsync(
        StrategyContext baseContext,
        ExtraContextRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Snapshot of extra context collected on demand for matching and execution.
/// </summary>
public sealed record ExtraContextSnapshot
{
    /// <summary>
    /// Root nodes exposed to strategy expressions, keyed by namespace such as <c>file_manager</c>.
    /// </summary>
    /// <remarks>
    /// Strategy paths are rooted under <c>extra</c>; for example <c>extra.file_manager.selection.items</c>.
    /// </remarks>
    public IReadOnlyDictionary<string, ExtraContextNode> Roots { get; init; } =
        new Dictionary<string, ExtraContextNode>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<StrategyDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Tree node for values exposed under the public <c>extra.*</c> strategy namespace.
/// </summary>
public sealed record ExtraContextNode
{
    /// <summary>
    /// Scalar or structured value at this node.
    /// </summary>
    /// <remarks>
    /// A node may have both <see cref="Value"/> and <see cref="Children"/> when a provider wants to expose a summary value and addressable subfields.
    /// </remarks>
    public object? Value { get; init; }

    /// <summary>
    /// Named child nodes for dotted path access.
    /// </summary>
    public IReadOnlyDictionary<string, ExtraContextNode> Children { get; init; } =
        new Dictionary<string, ExtraContextNode>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when this node has a non-null value in addition to any children.
    /// </summary>
    public bool HasValue => Value is not null;
}
