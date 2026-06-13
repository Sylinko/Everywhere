using Everywhere.Chat.Plugins;
using Everywhere.Common;
using MessagePack;

namespace Everywhere.StrategyEngine;

/// <summary>
/// A strategy defines an intent, how to invoke it, and the resulting prompt templates.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial record Strategy
{
    /// <summary>
    /// Unique identifier for deduplication across strategies, for example <c>builtin.browser-summarize</c>.
    /// </summary>
    [Key(0)] public required string Id { get; init; }

    /// <summary>
    /// Display name.
    /// </summary>
    [Key(1)] public required IDynamicResourceKey NameKey { get; init; }

    /// <summary>
    /// Optional description shown as tooltip or subtitle.
    /// </summary>
    public IDynamicResourceKey? DescriptionKey { get; init; }

    /// <summary>
    /// Icon for UI display.
    /// </summary>
    public ColoredIcon? Icon { get; init; }

    /// <summary>
    /// Priority for override and sorting (higher = more prominent position and overrides lower priority duplicates).
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// Concrete source of this normalized strategy.
    /// </summary>
    /// <remarks>
    /// For a file derived through <c>from</c>, this remains the current file; included sources are recorded in <see cref="Includes"/>.
    /// </remarks>
    public StrategySource Source { get; init; } = StrategySource.Unknown;

    /// <summary>
    /// Sources included through authoring-time references such as <c>from</c>.
    /// </summary>
    public IReadOnlyList<StrategySource> Includes { get; init; } = [];

    /// <summary>
    /// Condition that must be satisfied for this strategy to be available to the user.
    /// </summary>
    public IStrategyCondition? Condition { get; init; }

    /// <summary>
    /// User message template to send when starting the conversation.
    /// </summary>
    /// <remarks>
    /// Supports variable interpolation such as <c>{extra.file_manager.selection.items}</c> after preprocessors and extra context are collected.
    /// </remarks>
    [Key(2)] public string? Body { get; init; }

    /// <summary>
    /// System prompt template for the agent session. Overrides the default prompt.
    /// Supports variable interpolation with {variable} syntax.
    /// Leave null to use the default system prompt.
    /// </summary>
    [Key(3)] public string? SystemPrompt { get; init; }

    /// <summary>
    /// Request-scoped tool rules. <c>null</c> means the current default tool policy is left unchanged.
    /// </summary>
    /// <remarks>
    /// Wildcards are allowed, for example
    /// <c>{ "builtin.visual_tree.*": true, "builtin.web_browser.web_*": true, "builtin.web_browser.web_search": false }</c>.
    ///
    /// <c>builtin.visual_tree.*</c> and <c>builtin.visual_tree</c> are different. The former applies to child functions;
    /// the latter applies only to the named tool group and leaves child function state unchanged.
    ///
    /// Rules are applied in deterministic order; later matching rules override earlier ones.
    /// </remarks>
    [Key(4)] public ToolRulesets? ToolRulesets { get; init; }

    /// <summary>
    /// Preprocessor IDs to run before prompt rendering.
    /// </summary>
    /// <remarks>
    /// IDs are resolved from registered preprocessors. Unknown IDs should become diagnostics when the execution pipeline is wired.
    /// </remarks>
    [IgnoreMember]
    public IReadOnlyList<string> Preprocessors
    {
        get => _preprocessors ?? [];
        init => _preprocessors = value;
    }

    [Key(5)]
    private IReadOnlyList<string>? _preprocessors;

    /// <summary>
    /// Displays in the watermark as a hint for the user input after selecting this command.
    /// </summary>
    [Key(6)] public IDynamicResourceKey? ArgumentHintKey { get; init; }

    /// <summary>
    /// Runtime timeout options for matching and execution.
    /// </summary>
    public StrategyOptions Options { get; init; } = StrategyOptions.Default;

    /// <summary>
    /// Additional normalized metadata for diagnostics, editor surfaces, and future providers.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
}
