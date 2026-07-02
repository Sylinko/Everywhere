using System.Text.RegularExpressions;
using ZLinq;

namespace Everywhere.AI.Prompts;

/// <summary>
/// A placeholder occurrence found in a prompt template.
/// </summary>
/// <remarks>
/// <see cref="RawText"/> preserves the exact matched text, including braces, so editor and preview
/// layers do not need to reconstruct source text from <see cref="Name"/>.
/// </remarks>
public readonly record struct PromptPlaceholderToken(string Name, string RawText, PromptTextSpan Span);

/// <summary>
/// Parses Prompt Manager placeholder syntax without resolving values.
/// </summary>
/// <remarks>
/// The grammar intentionally matches the existing chat renderer: single-braced word placeholders
/// such as <c>{Date}</c> are tokens, while escaped or nested-looking forms such as <c>{{Date}}</c>
/// and <c>{{{Date}}}</c> remain literal text.
/// </remarks>
/// <example>
/// <code>
/// PromptTemplateParser.ParsePlaceholders("Today is {Date}");
/// // returns one token named "Date" with span covering "{Date}"
/// </code>
/// </example>
public static partial class PromptTemplateParser
{
    internal const string PlaceholderPattern = @"(?<!\{)\{(\w+)\}(?!\})";
    internal const int PlaceholderNameGroup = 1;

    /// <summary>
    /// Returns all placeholder tokens in source order.
    /// </summary>
    public static IReadOnlyList<PromptPlaceholderToken> ParsePlaceholders(string template)
    {
        if (string.IsNullOrEmpty(template)) return [];

        var matches = PromptTemplateRegex().Matches(template);
        if (matches.Count == 0) return [];

        var placeholders = new List<PromptPlaceholderToken>(matches.Count);
        foreach (var match in matches.AsValueEnumerable())
        {
            var name = match.Groups[PlaceholderNameGroup].Value;
            placeholders.Add(
                new PromptPlaceholderToken(
                    name,
                    match.Value,
                    new PromptTextSpan(match.Index, match.Length)));
        }

        return placeholders;
    }

    /// <summary>
    /// Checks whether a template contains a placeholder with the exact case-sensitive name.
    /// </summary>
    public static bool ContainsPlaceholder(string template, string name)
    {
        return ParsePlaceholders(template).AsValueEnumerable().Any(placeholder => StringComparer.Ordinal.Equals(placeholder.Name, name));
    }

    [GeneratedRegex(PlaceholderPattern)]
    private static partial Regex PromptTemplateRegex();
}