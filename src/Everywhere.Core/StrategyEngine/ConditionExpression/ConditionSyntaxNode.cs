using System.Globalization;
using System.Text;
using Everywhere.StrategyEngine.ConditionExpression.Syntax;

namespace Everywhere.StrategyEngine.ConditionExpression;

/// <summary>
/// Canonical syntax tree produced by the Condition DSL frontend.
/// </summary>
/// <remarks>
/// Syntax nodes preserve parsed values and normalized dotted-path structure, but they do not know the schema or
/// operator types. Semantic binding turns these nodes into <see cref="ConditionNode"/> instances.
/// </remarks>
internal abstract record ConditionSyntaxNode(string Path)
{
    /// <summary>
    /// Appends a deterministic YAML-like representation of this node.
    /// </summary>
    public abstract void AppendCanonical(StringBuilder builder, int indent);

    /// <summary>
    /// Returns the stable canonical representation used by diagnostics and explain output.
    /// </summary>
    public string ToCanonicalString()
    {
        var builder = new StringBuilder();
        AppendCanonical(builder, 0);
        return builder.TrimEnd().ToString();
    }

    protected static string Indent(int indent) => new(' ', indent);

    protected static string FormatScalar(object? value) =>
        value switch
        {
            null => "null",
            bool boolean => boolean ? "true" : "false",
            string text => QuoteString(text),
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => QuoteString(value.ToString() ?? string.Empty)
        };

    private static string QuoteString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

/// <summary>
/// Scalar syntax value from the authoring document.
/// </summary>
/// <remarks>
/// YAML ambiguous values are represented by the parser's concrete CLR value, so explain output can show whether
/// an author wrote a string, number, boolean, or null.
/// </remarks>
internal sealed record ConditionScalarSyntaxNode(object? Value, string ScalarPath) : ConditionSyntaxNode(ScalarPath)
{
    public override void AppendCanonical(StringBuilder builder, int indent) =>
        builder.Append(Indent(indent)).Append(FormatScalar(Value)).Append('\n');
}

/// <summary>
/// Sequence syntax node, usually used by logical groups or collection operands.
/// </summary>
/// <example>
/// <c>any: [ ... ]</c> and <c>in: [".md", ".txt"]</c> both pass through this representation before binding.
/// </example>
internal sealed record ConditionSequenceSyntaxNode(
    IReadOnlyList<ConditionSyntaxNode> Items,
    string SequencePath) : ConditionSyntaxNode(SequencePath)
{
    public override void AppendCanonical(StringBuilder builder, int indent)
    {
        foreach (var item in Items)
        {
            builder.Append(Indent(indent)).Append('-');
            if (item is ConditionScalarSyntaxNode scalar)
            {
                builder.Append(' ').Append(FormatScalar(scalar.Value)).Append('\n');
                continue;
            }

            builder.Append('\n');
            item.AppendCanonical(builder, indent + 2);
        }
    }
}

/// <summary>
/// Mapping syntax node after dotted-path normalization.
/// </summary>
/// <remarks>
/// A key such as <c>attachments.files.count</c> is normalized into nested mapping nodes before binding so the
/// binder only needs to process one path segment at a time.
/// </remarks>
internal sealed record ConditionMappingSyntaxNode(
    IReadOnlyDictionary<string, ConditionSyntaxNode> Children,
    string MappingPath) : ConditionSyntaxNode(MappingPath)
{
    public override void AppendCanonical(StringBuilder builder, int indent)
    {
        foreach (var (key, value) in Children.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            builder.Append(Indent(indent)).Append(key).Append(':');
            if (value is ConditionScalarSyntaxNode scalar)
            {
                builder.Append(' ').Append(FormatScalar(scalar.Value)).Append('\n');
                continue;
            }

            builder.Append('\n');
            value.AppendCanonical(builder, indent + 2);
        }
    }
}
