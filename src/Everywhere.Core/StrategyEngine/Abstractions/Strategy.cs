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
    /// Unique identifier for deduplication across strategies (e.g., 'builtin.browser-summarize').
    /// </summary>
    [Key(0)] public required string Id { get; init; }

    /// <summary>
    /// Display name.
    /// </summary>
    [Key(1)] public required IDynamicLocaleKey NameKey { get; init; }

    /// <summary>
    /// Optional description shown as tooltip or subtitle.
    /// </summary>
    public IDynamicLocaleKey? DescriptionKey { get; init; }

    /// <summary>
    /// Icon for UI display.
    /// </summary>
    public ColoredIcon? Icon { get; init; }

    /// <summary>
    /// Priority for override and sorting (higher = more prominent position and overrides lower priority duplicates).
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// Condition that must be satisfied for this strategy to be available to the user.
    /// </summary>
    public IStrategyCondition? Condition { get; init; }

    /// <summary>
    /// User message template to auto-send when starting the conversation.
    /// Supports variable interpolation with {variable} syntax (e.g. from Preprocessors).
    /// </summary>
    [Key(2)] public string? Body { get; init; }

    /// <summary>
    /// System prompt template for the agent session. Overrides the default prompt.
    /// Supports variable interpolation with {variable} syntax.
    /// Leave null to use the default system prompt.
    /// </summary>
    [Key(3)] public string? SystemPrompt { get; init; }

    /// <summary>
    /// Allowed tool/plugin names for this command. null for default.
    /// </summary>
    /// <remarks>
    /// The outer patterns match plugin keys and the inner patterns match function names.
    /// More-specific patterns override less-specific patterns.
    /// </remarks>
    [Key(4)] public ToolPatternRulesets? ToolPatternRulesets { get; init; }

    /// <summary>
    /// List of preprocessor IDs to run before executing this strategy.
    /// </summary>
    [Key(5)] public IReadOnlyList<string>? Preprocessors { get; init; }

    /// <summary>
    /// Displays in the watermark as a hint for the user input after selecting this command.
    /// </summary>
    [Key(6)] public IDynamicLocaleKey? ArgumentHintKey { get; init; }
}
