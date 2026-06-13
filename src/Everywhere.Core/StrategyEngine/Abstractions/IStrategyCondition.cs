using Everywhere.Chat;

namespace Everywhere.StrategyEngine;

/// <summary>
/// A condition that can be evaluated against a strategy context.
/// Conditions are composable building blocks for strategy matching.
/// </summary>
public interface IStrategyCondition
{
    /// <summary>
    /// Evaluates this condition against the given context.
    /// </summary>
    /// <param name="context">The strategy context to evaluate against.</param>
    /// <returns><c>true</c> when matched, <c>false</c> when not matched, or <c>null</c> when required data is unavailable.</returns>
    bool? Evaluate(StrategyContext context);

    /// <summary>
    /// Evaluates this condition and returns diagnostics when the implementation can provide them.
    /// </summary>
    ConditionEvaluationResult EvaluateDetailed(StrategyContext context) => new()
    {
        Value = Evaluate(context)
    };
}

/// <summary>
/// A condition that matches a specific attachment in the context.
/// </summary>
public interface IAttachmentCondition : IStrategyCondition
{
    /// <summary>
    /// The type of attachment this condition applies to.
    /// </summary>
    AttachmentType TargetType { get; }

    /// <summary>
    /// If true, at least one matching attachment must be primary.
    /// </summary>
    bool IsPrimaryRequired { get; init; }
}

/// <summary>
/// Types of attachments that can be matched by conditions.
/// </summary>
public enum AttachmentType
{
    /// <summary>
    /// Matches any attachment type.
    /// </summary>
    Any,

    /// <summary>
    /// Matches <see cref="VisualElementAttachment"/>.
    /// </summary>
    VisualElement,

    /// <summary>
    /// Matches <see cref="TextSelectionAttachment"/>.
    /// </summary>
    TextSelection,

    /// <summary>
    /// Matches <see cref="TextAttachment"/>.
    /// </summary>
    Text,

    /// <summary>
    /// Matches <see cref="FileAttachment"/>.
    /// </summary>
    File
}
