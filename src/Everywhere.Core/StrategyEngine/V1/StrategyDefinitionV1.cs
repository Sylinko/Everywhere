namespace Everywhere.StrategyEngine;

/// <summary>
/// Versioned authoring model for <c>everywhere.strategy/v1</c>.
/// </summary>
public sealed record StrategyDefinitionV1
{
    public const string DefaultSchema = "everywhere.strategy/v1";

    /// <summary>
    /// Version discriminator from the frontmatter <c>schema</c> field.
    /// </summary>
    public string Schema { get; init; } = DefaultSchema;

    /// <summary>
    /// Stable authoring ID before provider namespacing.
    /// </summary>
    /// <remarks>
    /// A user file may declare <c>id: research.summary</c>; normalization prefixes it with the provider namespace when needed.
    /// </remarks>
    public string? Id { get; init; }

    /// <summary>
    /// Optional source to derive this definition from.
    /// </summary>
    /// <remarks>
    /// <c>from</c> is authoring-time composition. It is resolved during normalization and recorded on runtime <see cref="Strategy.Includes"/>.
    /// </remarks>
    public StrategyFromReference? From { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// Icon identifier as authored, usually a Lucide icon name such as <c>FileText</c>.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Relative ordering hint before normalization. Higher values are shown earlier.
    /// </summary>
    public int? Priority { get; init; }

    /// <summary>
    /// Raw condition object from YAML.
    /// </summary>
    /// <remarks>
    /// M5 compiles this into <see cref="Strategy.Condition"/>. Until then, scalar booleans are supported and structured objects are preserved for diagnostics.
    /// </remarks>
    public object? When { get; init; }

    /// <summary>
    /// Tool rule map where keys are tool/plugin patterns and values enable or disable them.
    /// </summary>
    /// <remarks>
    /// Example: <c>builtin.web_browser.web_search: false</c>.
    /// </remarks>
    public IReadOnlyDictionary<string, bool>? Tools { get; init; }

    /// <summary>
    /// Preprocessor IDs to run before prompt rendering.
    /// </summary>
    public IReadOnlyList<string>? Preprocessors { get; init; }

    /// <summary>
    /// Request-scoped system prompt override.
    /// </summary>
    public string? SystemPrompt { get; init; }

    public StrategyOptionsDefinitionV1? Options { get; init; }

    /// <summary>
    /// Markdown body after frontmatter, or the optional frontmatter <c>body</c> field.
    /// </summary>
    /// <remarks>
    /// During <c>from</c> merge, an omitted body section inherits the source body; an explicit body section replaces it, even if empty.
    /// </remarks>
    public string? Body { get; init; }

    /// <summary>
    /// Unknown frontmatter fields preserved for editor surfaces and future extensions.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Authoring-time option values as strings so validation can report invalid durations.
/// </summary>
public sealed record StrategyOptionsDefinitionV1
{
    /// <summary>
    /// Total matching budget, for example <c>300ms</c>.
    /// </summary>
    public string? MatchingTimeout { get; init; }

    /// <summary>
    /// Per-condition evaluation budget, for example <c>80ms</c>.
    /// </summary>
    public string? ConditionTimeout { get; init; }

    /// <summary>
    /// Regex condition budget, for example <c>50ms</c>.
    /// </summary>
    public string? RegexTimeout { get; init; }

    /// <summary>
    /// Visual query budget, for example <c>120ms</c>.
    /// </summary>
    public string? VisualQueryTimeout { get; init; }

    /// <summary>
    /// Extra context collection budget, for example <c>200ms</c>.
    /// </summary>
    public string? ExtraTimeout { get; init; }
}
