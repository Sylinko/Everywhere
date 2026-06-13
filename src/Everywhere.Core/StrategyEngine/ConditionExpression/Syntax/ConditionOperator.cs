using System.Globalization;
using System.Text.RegularExpressions;

namespace Everywhere.StrategyEngine.ConditionExpression.Syntax;

/// <summary>
/// Bound, executable condition operator.
/// </summary>
/// <remarks>
/// Operator names are used only for diagnostics and explain output. Runtime dispatch is polymorphic; the binder
/// is responsible for mapping author-facing DSL names such as <c>startsWith</c> to concrete operator classes.
/// </remarks>
internal abstract class ConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType
)
{
    /// <summary>
    /// Canonical path of the operator operand in the original DSL.
    /// </summary>
    /// <example><c>when.attachments.files.count.min</c></example>
    public string NodePath { get; } = nodePath;

    /// <summary>
    /// Static type of the path value this operator was bound against.
    /// </summary>
    public ConditionValueType TargetType { get; } = targetType;

    /// <summary>
    /// Static type inferred from the operator operand.
    /// </summary>
    public ConditionValueType OperandType { get; } = operandType;

    /// <summary>
    /// Public DSL name used in diagnostics and explain output.
    /// </summary>
    public abstract string OperatorName { get; }

    /// <summary>
    /// Evaluates the operator against a resolved path value.
    /// </summary>
    /// <param name="target"></param>
    /// <param name="context"></param>
    /// <param name="comparison">
    /// String comparison selected by sibling modifiers such as <c>caseSensitive</c>.
    /// </param>
    public abstract bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison);

    internal static bool RuntimeMismatch(ConditionEvaluationContext context, string path, string message)
    {
        context.AddDiagnostic(StrategyDiagnosticSeverity.Warning, "condition.runtime_type_mismatch", message, path);
        return false;
    }
}

/// <summary>
/// Base class for operators whose operand is represented as a scalar, sequence, or mapping value.
/// </summary>
/// <remarks>
/// Examples include <c>equals: "code"</c>, <c>in: [".md", ".txt"]</c>, and
/// <c>count: { min: 1 }</c>.
/// </remarks>
internal abstract class ScalarOperandConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : ConditionOperator(nodePath, targetType, operandType)
{
    public object? Operand { get; } = operand;
}

/// <summary>
/// Modifier operator that changes sibling string comparison behavior.
/// </summary>
/// <remarks>
/// This operator is skipped as a predicate during path evaluation; its value is consumed when choosing
/// <see cref="StringComparison"/>.
/// </remarks>
internal sealed class CaseSensitiveConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    bool isCaseSensitive
) : ConditionOperator(nodePath, targetType, operandType)
{
    public bool IsCaseSensitive { get; } = isCaseSensitive;

    public override string OperatorName => "caseSensitive";

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison) => true;
}

/// <summary>
/// Scalar equality operator.
/// </summary>
/// <remarks>
/// Strings use the comparison mode selected for the containing path. Numeric values are compared after safe
/// decimal conversion.
/// </remarks>
internal sealed class EqualsConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : ScalarOperandConditionOperator(nodePath, targetType, operandType, operand)
{
    public override string OperatorName => "equals";

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison) =>
        target is null ? null : ConditionOperatorRuntime.CompareEquals(target, Operand, comparison);
}

/// <summary>
/// Scalar membership operator.
/// </summary>
/// <remarks>
/// Evaluates whether the resolved scalar target equals at least one item from the operand collection.
/// </remarks>
internal sealed class InConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : ScalarOperandConditionOperator(nodePath, targetType, operandType, operand)
{
    public override string OperatorName => "in";

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison)
    {
        if (target is null)
        {
            return null;
        }

        if (!ConditionRuntimeValues.TryMaterializeCollection(Operand, out var items))
        {
            return RuntimeMismatch(context, NodePath, "Operator 'in' expects a collection operand.");
        }

        return items.AsValueEnumerable().Any(item => ConditionOperatorRuntime.CompareEquals(target, item, comparison));
    }
}

/// <summary>
/// String or scalar-collection containment operator.
/// </summary>
/// <remarks>
/// On strings this means substring containment. On collections this means membership of one scalar operand.
/// Object collections are rejected during binding unless the target type is unknown.
/// </remarks>
internal sealed class ContainsConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : ScalarOperandConditionOperator(nodePath, targetType, operandType, operand)
{
    public override string OperatorName => "contains";

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison)
    {
        if (target is null)
        {
            return null;
        }

        if (target is string text)
        {
            if (Operand is not string value)
            {
                return RuntimeMismatch(context, NodePath, "String operator 'contains' expects a string operand.");
            }

            return text.Contains(value, comparison);
        }

        if (!ConditionRuntimeValues.TryMaterializeCollection(target, out var items))
        {
            return RuntimeMismatch(context, NodePath, "Operator 'contains' expects a collection target.");
        }

        return items.AsValueEnumerable().Any(item => ConditionOperatorRuntime.CompareEquals(item!, Operand, comparison));
    }
}

/// <summary>
/// Scalar collection overlap operator.
/// </summary>
/// <remarks>
/// Returns true when at least one operand item exists in the target collection. An empty operand collection is a
/// statically diagnosed false condition.
/// </remarks>
internal sealed class ContainsAnyConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : ScalarOperandConditionOperator(nodePath, targetType, operandType, operand)
{
    public override string OperatorName => "containsAny";

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison)
    {
        if (target is null)
        {
            return null;
        }

        if (!ConditionRuntimeValues.TryMaterializeCollection(target, out var targetItems) ||
            !ConditionRuntimeValues.TryMaterializeCollection(Operand, out var operandItems))
        {
            return RuntimeMismatch(context, NodePath, "Operator 'containsAny' expects collection target and operand.");
        }

        return operandItems.Count > 0 &&
            operandItems.AsValueEnumerable().Any(operandItem =>
                targetItems.AsValueEnumerable().Any(targetItem => ConditionOperatorRuntime.CompareEquals(targetItem!, operandItem, comparison)));
    }
}

/// <summary>
/// Scalar collection superset operator.
/// </summary>
/// <remarks>
/// Returns true only when every operand item exists in the target collection. Empty operands are allowed but
/// reported by static analysis because they are usually accidental.
/// </remarks>
internal sealed class ContainsAllConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : ScalarOperandConditionOperator(nodePath, targetType, operandType, operand)
{
    public override string OperatorName => "containsAll";

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison)
    {
        if (target is null)
        {
            return null;
        }

        if (!ConditionRuntimeValues.TryMaterializeCollection(target, out var targetItems) ||
            !ConditionRuntimeValues.TryMaterializeCollection(Operand, out var operandItems))
        {
            return RuntimeMismatch(context, NodePath, "Operator 'containsAll' expects collection target and operand.");
        }

        return operandItems.AsValueEnumerable().All(operandItem =>
            targetItems.AsValueEnumerable().Any(targetItem => ConditionOperatorRuntime.CompareEquals(targetItem!, operandItem, comparison)));
    }
}

/// <summary>
/// Base class for string predicate operators.
/// </summary>
/// <remarks>
/// The helper centralizes runtime type mismatch diagnostics so individual string operators only provide the
/// predicate.
/// </remarks>
internal abstract class StringConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : ScalarOperandConditionOperator(nodePath, targetType, operandType, operand)
{
    protected bool? EvaluateStringPredicate(
        object? target,
        ConditionEvaluationContext context,
        StringComparison comparison,
        Func<string, string, StringComparison, bool> predicate)
    {
        if (target is null)
        {
            return null;
        }

        if (target is not string text || Operand is not string value)
        {
            return RuntimeMismatch(context, NodePath, $"Operator '{OperatorName}' expects a string target and string operand.");
        }

        return predicate(text, value, comparison);
    }
}

/// <summary>
/// String prefix operator.
/// </summary>
internal sealed class StartsWithConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : StringConditionOperator(nodePath, targetType, operandType, operand)
{
    public override string OperatorName => "startsWith";

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison) =>
        EvaluateStringPredicate(target, context, comparison, static (left, right, cmp) => left.StartsWith(right, cmp));
}

/// <summary>
/// String suffix operator.
/// </summary>
internal sealed class EndsWithConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : StringConditionOperator(nodePath, targetType, operandType, operand)
{
    public override string OperatorName => "endsWith";

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison) =>
        EvaluateStringPredicate(target, context, comparison, static (left, right, cmp) => left.EndsWith(right, cmp));
}

/// <summary>
/// Regex string operator with runtime timeout support.
/// </summary>
/// <remarks>
/// Invalid regex patterns are usually rejected during binding. Runtime still catches invalid/timeout cases so
/// unknown or generated operands cannot escape diagnostics.
/// </remarks>
internal sealed class RegexConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : ScalarOperandConditionOperator(nodePath, targetType, operandType, operand)
{
    public override string OperatorName => "regex";

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison)
    {
        if (target is null)
        {
            return null;
        }

        if (target is not string text || Operand is not string pattern)
        {
            return RuntimeMismatch(context, NodePath, "Operator 'regex' expects a string target and pattern.");
        }

        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.None, context.Options.RegexTimeout);
        }
        catch (RegexMatchTimeoutException ex)
        {
            context.AddDiagnostic(
                StrategyDiagnosticSeverity.Warning,
                "regex.timeout",
                "Regex condition timed out.",
                NodePath,
                ex,
                context.Options.RegexTimeout);
            return null;
        }
        catch (ArgumentException ex)
        {
            context.AddDiagnostic(StrategyDiagnosticSeverity.Warning, "regex.invalid", $"Regex pattern is invalid: {ex.Message}", NodePath, ex);
            return false;
        }
    }
}

/// <summary>
/// Shell-style glob operator for complete string matches.
/// </summary>
/// <remarks>
/// The DSL supports <c>*</c> and <c>?</c>. The operator lowers the glob to a regex but reports timeout through
/// the same <c>regex.timeout</c> diagnostic family.
/// </remarks>
internal sealed class GlobConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : ScalarOperandConditionOperator(nodePath, targetType, operandType, operand)
{
    public override string OperatorName => "glob";

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison)
    {
        if (target is null)
        {
            return null;
        }

        if (target is not string text || Operand is not string glob)
        {
            return RuntimeMismatch(context, NodePath, "Operator 'glob' expects a string target and pattern.");
        }

        var options = comparison == StringComparison.Ordinal ? RegexOptions.None : RegexOptions.IgnoreCase;
        var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
        try
        {
            return Regex.IsMatch(text, pattern, options, context.Options.RegexTimeout);
        }
        catch (RegexMatchTimeoutException ex)
        {
            context.AddDiagnostic(
                StrategyDiagnosticSeverity.Warning,
                "regex.timeout",
                "Glob condition timed out.",
                NodePath,
                ex,
                context.Options.RegexTimeout);
            return null;
        }
    }
}

/// <summary>
/// String length boundary operator.
/// </summary>
/// <remarks>
/// The operand is a <c>min</c>/<c>max</c> map, for example <c>length: { min: 3, max: 80 }</c>.
/// </remarks>
internal sealed class LengthConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : ScalarOperandConditionOperator(nodePath, targetType, operandType, operand)
{
    public override string OperatorName => "length";

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison)
    {
        if (target is null)
        {
            return null;
        }

        return target is string text ?
            ConditionOperatorRuntime.BoundedNumber(text.Length, Operand, context, NodePath, OperatorName) :
            RuntimeMismatch(context, NodePath, "Operator 'length' expects a string target.");
    }
}

/// <summary>
/// Base class for numeric boundary operators.
/// </summary>
internal abstract class NumberBoundaryConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : ScalarOperandConditionOperator(nodePath, targetType, operandType, operand)
{
    protected abstract bool Matches(decimal left, decimal right);

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison)
    {
        if (target is null)
        {
            return null;
        }

        if (!ConditionOperatorRuntime.TryConvertNumber(target, out var left) ||
            !ConditionOperatorRuntime.TryConvertNumber(Operand, out var right))
        {
            return RuntimeMismatch(context, NodePath, $"Operator '{OperatorName}' expects numeric values.");
        }

        return Matches(left, right);
    }
}

/// <summary>
/// Numeric lower-bound operator.
/// </summary>
internal sealed class MinConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : NumberBoundaryConditionOperator(nodePath, targetType, operandType, operand)
{
    public override string OperatorName => "min";

    protected override bool Matches(decimal left, decimal right) => left >= right;
}

/// <summary>
/// Numeric upper-bound operator.
/// </summary>
internal sealed class MaxConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : NumberBoundaryConditionOperator(nodePath, targetType, operandType, operand)
{
    public override string OperatorName => "max";

    protected override bool Matches(decimal left, decimal right) => left <= right;
}

/// <summary>
/// Collection count boundary operator.
/// </summary>
/// <remarks>
/// The operand shape mirrors <c>length</c>: <c>count: { min: 1, max: 5 }</c>.
/// </remarks>
internal sealed class CountConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    object? operand
) : ScalarOperandConditionOperator(nodePath, targetType, operandType, operand)
{
    public override string OperatorName => "count";

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison)
    {
        if (target is null)
        {
            return null;
        }

        if (!ConditionRuntimeValues.TryMaterializeCollection(target, out var items))
        {
            return RuntimeMismatch(context, NodePath, "Operator 'count' expects a collection target.");
        }

        return ConditionOperatorRuntime.BoundedNumber(items.Count, Operand, context, NodePath, OperatorName);
    }
}

/// <summary>
/// Base class for collection predicate operators.
/// </summary>
/// <remarks>
/// Predicate children are evaluated with the current collection element exposed through
/// <see cref="ConditionEvaluationContext.ItemScope"/>.
/// </remarks>
internal abstract class CollectionPredicateConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    IReadOnlyList<ConditionNode> predicateChildren
) : ConditionOperator(nodePath, targetType, operandType)
{
    /// <summary>
    /// Bound predicate evaluated for each collection element.
    /// </summary>
    /// <example><c>attachments.files.any.extension.in</c> stores the <c>$item.extension</c> predicate here.</example>
    public IReadOnlyList<ConditionNode> PredicateChildren { get; } = predicateChildren;

    protected abstract bool? EvaluateItems(IReadOnlyList<object?> items, ConditionEvaluationContext context);

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison)
    {
        if (target is null)
        {
            return null;
        }

        return ConditionRuntimeValues.TryMaterializeCollection(target, out var items) ?
            EvaluateItems(items, context) :
            RuntimeMismatch(context, NodePath, $"Operator '{OperatorName}' expects a collection target.");
    }

    protected bool? EvaluatePredicate(object? item, ConditionEvaluationContext context) =>
        ConditionLogicalSemantics.EvaluateAll(PredicateChildren, context.WithItemScope(item));
}

/// <summary>
/// Collection predicate that succeeds when any element matches.
/// </summary>
internal sealed class AnyCollectionConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    IReadOnlyList<ConditionNode> predicateChildren
)
    : CollectionPredicateConditionOperator(nodePath, targetType, operandType, predicateChildren)
{
    public override string OperatorName => "any";

    protected override bool? EvaluateItems(IReadOnlyList<object?> items, ConditionEvaluationContext context)
    {
        if (items.Count == 0)
        {
            return false;
        }

        var hasNull = false;
        foreach (var item in items)
        {
            var value = EvaluatePredicate(item, context);
            if (value is true)
            {
                return true;
            }

            hasNull |= value is null;
        }

        return hasNull ? null : false;
    }
}

/// <summary>
/// Collection predicate that succeeds when every element matches.
/// </summary>
/// <remarks>
/// Empty collections evaluate to false by DSL design, favoring user-facing usefulness over mathematical
/// vacuous truth.
/// </remarks>
internal sealed class AllCollectionConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    IReadOnlyList<ConditionNode> predicateChildren
)
    : CollectionPredicateConditionOperator(nodePath, targetType, operandType, predicateChildren)
{
    public override string OperatorName => "all";

    protected override bool? EvaluateItems(IReadOnlyList<object?> items, ConditionEvaluationContext context)
    {
        if (items.Count == 0)
        {
            return false;
        }

        var hasNull = false;
        foreach (var item in items)
        {
            var value = EvaluatePredicate(item, context);
            if (value is false)
            {
                return false;
            }

            hasNull |= value is null;
        }

        return hasNull ? null : true;
    }
}

/// <summary>
/// Collection predicate that succeeds when no element matches.
/// </summary>
internal sealed class NoneCollectionConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    IReadOnlyList<ConditionNode> predicateChildren
)
    : CollectionPredicateConditionOperator(nodePath, targetType, operandType, predicateChildren)
{
    public override string OperatorName => "none";

    protected override bool? EvaluateItems(IReadOnlyList<object?> items, ConditionEvaluationContext context)
    {
        if (items.Count == 0)
        {
            return true;
        }

        var hasNull = false;
        foreach (var item in items)
        {
            var value = EvaluatePredicate(item, context);
            if (value is true)
            {
                return false;
            }

            hasNull |= value is null;
        }

        return hasNull ? null : true;
    }
}

/// <summary>
/// Predicate-level negation operator used inside scoped operator maps.
/// </summary>
internal sealed class NotConditionOperator(
    string nodePath,
    ConditionValueType targetType,
    ConditionValueType operandType,
    IReadOnlyList<ConditionNode> predicateChildren
) : ConditionOperator(nodePath, targetType, operandType)
{
    public IReadOnlyList<ConditionNode> PredicateChildren { get; } = predicateChildren;

    public override string OperatorName => "not";

    public override bool? Evaluate(object? target, ConditionEvaluationContext context, StringComparison comparison) =>
        ConditionLogicalSemantics.Invert(ConditionLogicalSemantics.EvaluateAll(PredicateChildren, context));
}

/// <summary>
/// Shared runtime helpers for bound operators.
/// </summary>
/// <remarks>
/// These helpers intentionally contain primitive comparisons and conversions, not operator dispatch.
/// </remarks>
internal static class ConditionOperatorRuntime
{
    public static bool CompareEquals(object target, object? operand, StringComparison comparison)
    {
        if (target is string left && operand is string right)
        {
            return string.Equals(left, right, comparison);
        }

        if (TryConvertNumber(target, out var leftNumber) && TryConvertNumber(operand, out var rightNumber))
        {
            return leftNumber == rightNumber;
        }

        return Equals(target, operand);
    }

    public static bool? BoundedNumber(
        int value,
        object? operand,
        ConditionEvaluationContext context,
        string path,
        string op)
    {
        if (operand is not IReadOnlyDictionary<string, object?> map)
        {
            return ConditionOperator.RuntimeMismatch(context, path, $"Operator '{op}' expects a min/max mapping.");
        }

        foreach (var (key, raw) in map)
        {
            if (!TryConvertNumber(raw, out var boundary))
            {
                return ConditionOperator.RuntimeMismatch(context, path, $"Operator '{op}.{key}' expects a numeric operand.");
            }

            if (key == "min" && value < boundary)
            {
                return false;
            }

            if (key == "max" && value > boundary)
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryConvertNumber(object? value, out decimal result)
    {
        switch (value)
        {
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return true;
            case float single when !float.IsNaN(single) && !float.IsInfinity(single):
                result = Convert.ToDecimal(single, CultureInfo.InvariantCulture);
                return true;
            case double dbl when !double.IsNaN(dbl) && !double.IsInfinity(dbl):
                result = Convert.ToDecimal(dbl, CultureInfo.InvariantCulture);
                return true;
            default:
                result = 0;
                return false;
        }
    }
}