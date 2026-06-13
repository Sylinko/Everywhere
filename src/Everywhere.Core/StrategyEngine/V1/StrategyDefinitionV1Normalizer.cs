using System.Text.RegularExpressions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Common.Frontmatter;
using Everywhere.StrategyEngine.ConditionExpression;
using Everywhere.StrategyEngine.Conditions;
using Lucide.Avalonia;

namespace Everywhere.StrategyEngine;

public sealed partial class StrategyDefinitionV1Normalizer : IStrategyDefinitionNormalizer
{
    public string Schema => StrategyDefinitionV1.DefaultSchema;

    public async Task<StrategyNormalizationResult> NormalizeAsync(
        StrategyDocument document,
        StrategyLoadContext context,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<StrategyDiagnostic>(document.Diagnostics);
        if (document.Definition is not StrategyDefinitionV1 currentDefinition)
        {
            diagnostics.Add(
                CreateDiagnostic(
                    "strategy.unsupported_schema",
                    $"Strategy schema '{document.Schema}' is not supported.",
                    document.Source));
            return new StrategyNormalizationResult { Diagnostics = diagnostics };
        }

        StrategyDefinitionV1? sourceDefinition = null;
        StrategyDocument? sourceDocument = null;
        if (currentDefinition.From is { } from)
        {
            sourceDocument = await ResolveFromAsync(from, document.Source, context, diagnostics, cancellationToken);
            if (sourceDocument?.Definition is StrategyDefinitionV1 includedDefinition)
            {
                diagnostics.AddRange(sourceDocument.Diagnostics);
                if (includedDefinition.From is not null)
                {
                    diagnostics.Add(
                        CreateDiagnostic(
                            "strategy.nested_from",
                            "Nested strategy 'from' references are not supported in v1.",
                            sourceDocument.Source));
                }
                else
                {
                    sourceDefinition = includedDefinition;
                }
            }
        }

        if (diagnostics.Any(diagnostic => diagnostic.Severity == StrategyDiagnosticSeverity.Error))
        {
            return new StrategyNormalizationResult { Diagnostics = diagnostics };
        }

        var merged = Merge(sourceDefinition, currentDefinition, document.HasBodySection);
        var name = merged.Name.NullIfWhiteSpace();
        if (string.IsNullOrWhiteSpace(name))
        {
            diagnostics.Add(
                CreateDiagnostic(
                    "strategy.missing_name",
                    "Strategy name is required after normalization.",
                    document.Source,
                    path: "name"));
        }
        else
        {
            ValidateName(name, document.Source, diagnostics);
        }

        var options = ResolveOptions(merged.Options, document.Source, diagnostics);
        var icon = ResolveIcon(merged.Icon, document.Source, diagnostics);
        var condition = ResolveCondition(merged.When, document.Source, options, diagnostics);

        if (diagnostics.Any(diagnostic => diagnostic.Severity == StrategyDiagnosticSeverity.Error))
        {
            return new StrategyNormalizationResult { Diagnostics = diagnostics };
        }

        return new StrategyNormalizationResult
        {
            Strategy = new Strategy
            {
                Id = ResolveId(name!, document.Source),
                Source = document.Source,
                Includes = sourceDocument is null ? [] : [sourceDocument.Source],
                NameKey = merged.TitleKey ?? new DirectResourceKey(name),
                DescriptionKey = string.IsNullOrWhiteSpace(merged.Description) ? null : new DirectResourceKey(merged.Description),
                Icon = icon,
                Priority = merged.Priority ?? 0,
                Condition = condition,
                Body = merged.Body,
                SystemPrompt = merged.SystemPrompt,
                ToolRulesets = merged.Tools is null ?
                    null :
                    new ToolRulesets(merged.Tools.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)),
                Preprocessors = merged.Preprocessors ?? [],
                Options = options,
                Metadata = merged.Metadata
            },
            Diagnostics = diagnostics
        };
    }

    private static async Task<StrategyDocument?> ResolveFromAsync(
        StrategyFromReference reference,
        StrategySource currentSource,
        StrategyLoadContext context,
        List<StrategyDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var resolver = context.SourceResolvers.FirstOrDefault(candidate => candidate.CanResolve(reference, currentSource));
        if (resolver is null)
        {
            diagnostics.Add(
                CreateDiagnostic(
                    "strategy.invalid_from",
                    $"No strategy source resolver can resolve '{reference.Source}'.",
                    currentSource,
                    path: "from"));
            return null;
        }

        try
        {
            return await resolver.ResolveAsync(reference, currentSource, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            diagnostics.Add(
                CreateDiagnostic(
                    "strategy.invalid_from",
                    $"Failed to resolve strategy source '{reference.Source}'.",
                    currentSource,
                    ex,
                    "from"));
            return null;
        }
    }

    private static StrategyDefinitionV1 Merge(
        StrategyDefinitionV1? source,
        StrategyDefinitionV1 current,
        bool currentHasBodySection)
    {
        if (source is null)
        {
            return current;
        }

        return source with
        {
            Schema = current.Schema,
            From = current.From,
            Name = current.Name ?? source.Name,
            TitleKey = current.TitleKey ?? source.TitleKey,
            Description = current.Description ?? source.Description,
            Icon = current.Icon ?? source.Icon,
            Priority = current.Priority ?? source.Priority,
            When = current.When ?? source.When,
            Tools = current.Tools ?? source.Tools,
            Preprocessors = current.Preprocessors ?? source.Preprocessors,
            SystemPrompt = current.SystemPrompt ?? source.SystemPrompt,
            Options = MergeOptions(source.Options, current.Options),
            Body = currentHasBodySection ? current.Body : source.Body,
            Metadata = MergeMetadata(source.Metadata, current.Metadata)
        };
    }

    private static StrategyOptionsDefinitionV1? MergeOptions(
        StrategyOptionsDefinitionV1? source,
        StrategyOptionsDefinitionV1? current)
    {
        if (source is null) return current;
        if (current is null) return source;

        return new StrategyOptionsDefinitionV1
        {
            MatchingTimeout = current.MatchingTimeout ?? source.MatchingTimeout,
            ConditionTimeout = current.ConditionTimeout ?? source.ConditionTimeout,
            RegexTimeout = current.RegexTimeout ?? source.RegexTimeout,
            VisualQueryTimeout = current.VisualQueryTimeout ?? source.VisualQueryTimeout,
            ExtraTimeout = current.ExtraTimeout ?? source.ExtraTimeout,
            PreprocessorTimeout = current.PreprocessorTimeout ?? source.PreprocessorTimeout
        };
    }

    private static IReadOnlyDictionary<string, object?> MergeMetadata(
        IReadOnlyDictionary<string, object?> source,
        IReadOnlyDictionary<string, object?> current)
    {
        var result = new Dictionary<string, object?>(source, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in current)
        {
            result[key] = value;
        }

        return result;
    }

    private static void ValidateName(string name, StrategySource source, List<StrategyDiagnostic> diagnostics)
    {
        if (!StrategyNameRegex().IsMatch(name))
        {
            diagnostics.Add(
                CreateDiagnostic(
                    "strategy.invalid_name",
                    "Strategy name contains invalid characters.",
                    source,
                    path: "name"));
        }
    }

    private static string ResolveId(string name, StrategySource source) => $"{source.ProviderId}.{name}";

    private static StrategyOptions ResolveOptions(
        StrategyOptionsDefinitionV1? definition,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics)
    {
        if (definition is null) return StrategyOptions.Default;

        return new StrategyOptions
        {
            MatchingTimeout = ParseDuration(definition.MatchingTimeout, nameof(definition.MatchingTimeout), source, diagnostics) ??
                StrategyOptions.Default.MatchingTimeout,
            ConditionTimeout = ParseDuration(definition.ConditionTimeout, nameof(definition.ConditionTimeout), source, diagnostics) ??
                StrategyOptions.Default.ConditionTimeout,
            RegexTimeout = ParseDuration(definition.RegexTimeout, nameof(definition.RegexTimeout), source, diagnostics) ??
                StrategyOptions.Default.RegexTimeout,
            VisualQueryTimeout = ParseDuration(definition.VisualQueryTimeout, nameof(definition.VisualQueryTimeout), source, diagnostics) ??
                StrategyOptions.Default.VisualQueryTimeout,
            ExtraTimeout = ParseDuration(definition.ExtraTimeout, nameof(definition.ExtraTimeout), source, diagnostics) ??
                StrategyOptions.Default.ExtraTimeout,
            PreprocessorTimeout = ParseDuration(definition.PreprocessorTimeout, nameof(definition.PreprocessorTimeout), source, diagnostics) ??
                StrategyOptions.Default.PreprocessorTimeout
        };
    }

    private static TimeSpan? ParseDuration(
        string? value,
        string field,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (YamlValueReader.TryParseDuration(value, out var duration)) return duration;

        diagnostics.Add(
            CreateDiagnostic(
                "strategy.invalid_duration",
                $"Strategy option '{field}' has an invalid duration.",
                source,
                path: $"options.{field}"));
        return null;
    }

    private static ColoredIcon? ResolveIcon(string? icon, StrategySource source, List<StrategyDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        if (Enum.TryParse<LucideIconKind>(icon, ignoreCase: true, out var kind)) return kind;

        diagnostics.Add(
            CreateDiagnostic(
                "strategy.invalid_icon",
                $"Strategy icon '{icon}' is not a known Lucide icon.",
                source,
                path: "icon",
                severity: StrategyDiagnosticSeverity.Warning));
        return null;
    }

    private IStrategyCondition? ResolveCondition(
        object? when,
        StrategySource source,
        StrategyOptions options,
        List<StrategyDiagnostic> diagnostics)
    {
        return when switch
        {
            null => null,
            true => TrueCondition.Shared,
            false => FalseCondition.Shared,
            _ => new StrategyConditionCompiler(options).Compile(when, source, diagnostics)
        };
    }

    private static StrategyDiagnostic CreateDiagnostic(
        string code,
        string message,
        StrategySource source,
        Exception? exception = null,
        string? path = null,
        StrategyDiagnosticSeverity severity = StrategyDiagnosticSeverity.Error) =>
        new()
        {
            Severity = severity,
            Code = code,
            MessageKey = new DirectResourceKey(message),
            Path = path ?? source.Location.LocalPath,
            ProviderId = source.ProviderId,
            Exception = exception
        };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex StrategyNameRegex();

}

file static class StrategyDefinitionNormalizerStringExtensions
{
    public static string? NullIfWhiteSpace(this string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
