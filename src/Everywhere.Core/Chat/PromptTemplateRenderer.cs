using System.Text.RegularExpressions;

namespace Everywhere.Chat;

/// <summary>
/// Resolves <c>{Variable}</c> placeholders in a prompt template against a resolver, recursively
/// expanding placeholders that appear inside resolved values. This lets a value such as
/// <see cref="Everywhere.AI.Prompts.DefaultSystemPrompt"/> (injected via the <c>{DefaultSystemPrompt}</c>
/// variable) have its own inner <c>{OS}</c>/<c>{Date}</c>/... placeholders resolved as well.
/// Resolution is guarded against infinite recursion.
/// </summary>
internal static partial class PromptTemplateRenderer
{
    /// <summary>
    /// Backstop limit on recursion depth. The path-scoped cycle guard is the primary protection;
    /// this additionally bounds stack/CPU for pathologically deep (but acyclic) variable chains.
    /// </summary>
    internal const int MaxDepth = 16;

    /// <summary>
    /// Renders <paramref name="template"/>, replacing each <c>{Name}</c> with <c>resolver(Name)</c>
    /// and recursively resolving any placeholders inside the substituted values.
    /// </summary>
    /// <param name="template">The prompt template containing <c>{Name}</c> placeholders.</param>
    /// <param name="resolver">
    /// Resolves a variable name to its value, or <see langword="null"/> if unknown. Unknown
    /// placeholders are left literal. Escaped <c>{{...}}</c> is never matched.
    /// </param>
    /// <returns>The fully resolved prompt.</returns>
    internal static string Render(string template, Func<string, string?> resolver) =>
        Expand(template, resolver, new HashSet<string>(StringComparer.Ordinal), 0);

    /// <summary>
    /// Single recursive expansion pass. The top-level template is matched once; recursion descends
    /// only into the resolved value of each match.
    /// </summary>
    private static string Expand(string text, Func<string, string?> resolver, HashSet<string> visiting, int depth)
    {
        return PromptTemplateRegex().Replace(
            text,
            match =>
            {
                var name = match.Groups[1].Value;
                var value = resolver(name);
                if (value is null) return match.Value;       // unknown variable -> leave the literal {Name}
                if (value.IndexOf('{') < 0) return value;    // leaf value (no placeholders) -> no recursion
                if (depth >= MaxDepth) return value;         // depth backstop -> substitute once, stop
                if (!visiting.Add(name)) return match.Value; // cycle on the current path -> leave literal
                try
                {
                    return Expand(value, resolver, visiting, depth + 1);
                }
                finally
                {
                    // Pop so sibling/repeat occurrences at the same level still expand.
                    visiting.Remove(name);
                }
            });
    }

    [GeneratedRegex(@"(?<!\{)\{(\w+)\}(?!\})")]
    private static partial Regex PromptTemplateRegex();
}
