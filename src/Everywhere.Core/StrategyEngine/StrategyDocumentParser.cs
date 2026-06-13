using Everywhere.Common.Frontmatter;

namespace Everywhere.StrategyEngine;

/// <summary>
/// Parses Markdown strategy files with YAML frontmatter into versioned strategy documents.
/// </summary>
public static class StrategyDocumentParser
{
    private static readonly HashSet<string> KnownDefinitionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "schema",
        "from",
        "name",
        "title",
        "description",
        "icon",
        "priority",
        "when",
        "tools",
        "preprocessors",
        "systemPrompt",
        "options",
        "body"
    };

    public static StrategyDocument Parse(string filePath, string rawContent, string providerId)
    {
        var diagnostics = new List<StrategyDiagnostic>();
        var source = new StrategySource
        {
            ProviderId = providerId,
            Location = CreateLocation(filePath),
            IsBuiltin = providerId.Equals("builtin", StringComparison.OrdinalIgnoreCase)
        };
        var markdownDocument = MarkdownFrontmatterParser.Parse(rawContent);

        if (!markdownDocument.HasFrontmatter)
        {
            diagnostics.Add(CreateDiagnostic(
                "strategy.missing_frontmatter",
                "Strategy file is missing YAML frontmatter.",
                filePath,
                providerId));
            var missingDefinition = new StrategyDefinitionV1 { Body = markdownDocument.Body };
            return new StrategyDocument
            {
                Source = source,
                Schema = missingDefinition.Schema,
                Definition = missingDefinition,
                Body = markdownDocument.Body,
                HasBodySection = false,
                Diagnostics = diagnostics
            };
        }

        var parseResult = YamlFrontmatterParser.ParseMapping(markdownDocument.RawFrontmatter ?? string.Empty);
        diagnostics.AddRange(parseResult.Diagnostics.Select(diagnostic => ConvertDiagnostic(diagnostic, filePath, providerId)));
        var values = parseResult.Values;
        var definition = values is null
            ? new StrategyDefinitionV1()
            : ConvertDefinition(values, markdownDocument.Body, filePath, providerId, diagnostics);

        return new StrategyDocument
        {
            Source = source,
            Schema = definition.Schema,
            Definition = definition,
            Body = markdownDocument.Body,
            HasBodySection = markdownDocument.HasBodySection,
            RawFrontmatter = markdownDocument.RawFrontmatter,
            Diagnostics = diagnostics
        };
    }

    private static StrategyDefinitionV1 ConvertDefinition(
        IReadOnlyDictionary<string, object?> values,
        string body,
        string filePath,
        string providerId,
        List<StrategyDiagnostic> diagnostics)
    {
        var metadata = values
            .Where(kvp => !KnownDefinitionKeys.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        var schema = ReadString(values, "schema", filePath, providerId, diagnostics) ?? StrategyDefinitionV1.DefaultSchema;
        var from = ReadFrom(values, filePath, providerId, diagnostics);
        var name = ReadString(values, "name", filePath, providerId, diagnostics);
        var title = ReadDynamicResourceKey(values, "title", filePath, providerId, diagnostics);
        var description = ReadString(values, "description", filePath, providerId, diagnostics);
        var icon = ReadString(values, "icon", filePath, providerId, diagnostics);
        var priority = ReadInt(values, "priority", filePath, providerId, diagnostics);
        var tools = ReadTools(values, filePath, providerId, diagnostics);
        var preprocessors = ReadStringList(values, "preprocessors", filePath, providerId, diagnostics);
        var systemPrompt = ReadString(values, "systemPrompt", filePath, providerId, diagnostics);
        var options = ReadOptions(values, filePath, providerId, diagnostics);
        var frontmatterBody = ReadString(values, "body", filePath, providerId, diagnostics);

        return new StrategyDefinitionV1
        {
            Schema = schema,
            From = from,
            Name = name,
            TitleKey = title,
            Description = description,
            Icon = icon,
            Priority = priority,
            When = values.GetValueOrDefault("when"),
            Tools = tools,
            Preprocessors = preprocessors,
            SystemPrompt = systemPrompt,
            Options = options,
            Body = frontmatterBody ?? body,
            Metadata = metadata
        };
    }

    private static string? ReadString(
        IReadOnlyDictionary<string, object?> values,
        string key,
        string filePath,
        string providerId,
        List<StrategyDiagnostic> diagnostics)
    {
        var commonDiagnostics = new List<FrontmatterDiagnostic>();
        var value = YamlValueReader.ReadString(values, key, commonDiagnostics);
        diagnostics.AddRange(commonDiagnostics.Select(diagnostic => ConvertDiagnostic(diagnostic, filePath, providerId)));
        return value;
    }

    private static int? ReadInt(
        IReadOnlyDictionary<string, object?> values,
        string key,
        string filePath,
        string providerId,
        List<StrategyDiagnostic> diagnostics)
    {
        var commonDiagnostics = new List<FrontmatterDiagnostic>();
        var value = YamlValueReader.ReadInt(values, key, commonDiagnostics);
        diagnostics.AddRange(commonDiagnostics.Select(diagnostic => ConvertDiagnostic(diagnostic, filePath, providerId)));
        return value;
    }

    private static IDynamicResourceKey? ReadDynamicResourceKey(
        IReadOnlyDictionary<string, object?> values,
        string key,
        string filePath,
        string providerId,
        List<StrategyDiagnostic> diagnostics)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value is string text)
        {
            return new DirectResourceKey(text);
        }

        if (value is not IReadOnlyDictionary<string, object?> map)
        {
            diagnostics.Add(CreateDiagnostic(
                "strategy.invalid_title",
                "Strategy title must be a string or a mapping from locale name to string.",
                filePath,
                providerId,
                path: "title"));
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in map.AsValueEnumerable())
        {
            if (string.IsNullOrWhiteSpace(k) || v is not string titleText)
            {
                diagnostics.Add(CreateDiagnostic(
                    "strategy.invalid_title",
                    "Strategy title mapping keys must be non-empty strings and values must be strings.",
                    filePath,
                    providerId,
                    path: string.IsNullOrWhiteSpace(k) ? "title" : $"title.{k}"));
                continue;
            }

            result[k] = titleText;
        }

        return diagnostics.Any(diagnostic => diagnostic.Code == "strategy.invalid_title") ?
            null :
            new JsonDynamicResourceKey(result);
    }

    private static StrategyFromReference? ReadFrom(
        IReadOnlyDictionary<string, object?> values,
        string filePath,
        string providerId,
        List<StrategyDiagnostic> diagnostics)
    {
        if (!values.TryGetValue("from", out var value)) return null;
        if (value is string source)
        {
            return new StrategyFromReference { Source = source };
        }

        var commonDiagnostics = new List<FrontmatterDiagnostic>();
        var map = YamlValueReader.ReadMap(values, "from", commonDiagnostics);
        diagnostics.AddRange(commonDiagnostics.Select(diagnostic => ConvertDiagnostic(diagnostic, filePath, providerId)));
        if (map is null)
        {
            return null;
        }

        var sourceValue = ReadString(map, "source", filePath, providerId, diagnostics);
        if (string.IsNullOrWhiteSpace(sourceValue))
        {
            diagnostics.Add(CreateDiagnostic(
                "strategy.invalid_from",
                "Strategy from reference is missing 'source'.",
                filePath,
                providerId,
                path: "from.source"));
            return null;
        }

        var kind = StrategyFromReferenceKind.Auto;
        var kindValue = ReadString(map, "kind", filePath, providerId, diagnostics);
        if (!string.IsNullOrWhiteSpace(kindValue) && !Enum.TryParse(kindValue, ignoreCase: true, out kind))
        {
            diagnostics.Add(CreateDiagnostic(
                "strategy.invalid_from",
                $"Strategy from kind '{kindValue}' is not supported.",
                filePath,
                providerId,
                path: "from.kind"));
            kind = StrategyFromReferenceKind.Auto;
        }

        return new StrategyFromReference
        {
            Source = sourceValue,
            Kind = kind
        };
    }

    private static IReadOnlyDictionary<string, bool>? ReadTools(
        IReadOnlyDictionary<string, object?> values,
        string filePath,
        string providerId,
        List<StrategyDiagnostic> diagnostics)
    {
        if (!values.TryGetValue("tools", out var value)) return null;
        if (value is not IReadOnlyDictionary<string, object?> map)
        {
            diagnostics.Add(CreateDiagnostic(
                "strategy.invalid_tools",
                "Strategy tools must be a mapping from string to bool.",
                filePath,
                providerId,
                path: "tools"));
            return null;
        }

        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in map.AsValueEnumerable())
        {
            if (v is bool isAllowed)
            {
                result[k] = isAllowed;
                continue;
            }

            diagnostics.Add(CreateDiagnostic(
                "strategy.invalid_tools",
                "Strategy tools values must be bool.",
                filePath,
                providerId,
                path: $"tools.{k}"));
        }

        return result;
    }

    private static IReadOnlyList<string>? ReadStringList(
        IReadOnlyDictionary<string, object?> values,
        string key,
        string filePath,
        string providerId,
        List<StrategyDiagnostic> diagnostics)
    {
        var commonDiagnostics = new List<FrontmatterDiagnostic>();
        var value = YamlValueReader.ReadStringList(values, key, commonDiagnostics);
        diagnostics.AddRange(commonDiagnostics.Select(diagnostic => ConvertDiagnostic(diagnostic, filePath, providerId)));
        return value;
    }

    private static StrategyOptionsDefinitionV1? ReadOptions(
        IReadOnlyDictionary<string, object?> values,
        string filePath,
        string providerId,
        List<StrategyDiagnostic> diagnostics)
    {
        var commonDiagnostics = new List<FrontmatterDiagnostic>();
        var map = YamlValueReader.ReadMap(values, "options", commonDiagnostics);
        diagnostics.AddRange(commonDiagnostics.Select(diagnostic => ConvertDiagnostic(diagnostic, filePath, providerId)));
        if (map is null) return null;

        var options = new StrategyOptionsDefinitionV1
        {
            MatchingTimeout = ReadDurationString(map, "matchingTimeout", filePath, providerId, diagnostics),
            ConditionTimeout = ReadDurationString(map, "conditionTimeout", filePath, providerId, diagnostics),
            RegexTimeout = ReadDurationString(map, "regexTimeout", filePath, providerId, diagnostics),
            VisualQueryTimeout = ReadDurationString(map, "visualQueryTimeout", filePath, providerId, diagnostics),
            ExtraTimeout = ReadDurationString(map, "extraTimeout", filePath, providerId, diagnostics),
            PreprocessorTimeout = ReadDurationString(map, "preprocessorTimeout", filePath, providerId, diagnostics)
        };

        return options;
    }

    private static string? ReadDurationString(
        IReadOnlyDictionary<string, object?> values,
        string key,
        string filePath,
        string providerId,
        List<StrategyDiagnostic> diagnostics)
    {
        var value = ReadString(values, key, filePath, providerId, diagnostics);
        if (value is null) return null;

        if (YamlValueReader.TryParseDuration(value, out _)) return value;

        diagnostics.Add(CreateDiagnostic(
            "strategy.invalid_duration",
            $"Strategy option '{key}' has an invalid duration.",
            filePath,
            providerId,
            path: $"options.{key}"));
        return value;
    }

    private static StrategyDiagnostic ConvertDiagnostic(FrontmatterDiagnostic diagnostic, string filePath, string providerId) =>
        CreateDiagnostic(
            diagnostic.Id switch
            {
                "frontmatter.invalid_yaml" => "strategy.invalid_yaml",
                "frontmatter.invalid_duration" => "strategy.invalid_duration",
                "frontmatter.invalid_field" => "strategy.invalid_field",
                _ => diagnostic.Id
            },
            diagnostic.Message,
            filePath,
            providerId,
            diagnostic.Exception,
            diagnostic.Path);

    private static StrategyDiagnostic CreateDiagnostic(
        string code,
        string message,
        string filePath,
        string providerId,
        Exception? exception = null,
        string? path = null) =>
        new()
        {
            Severity = StrategyDiagnosticSeverity.Error,
            Code = code,
            MessageKey = new DirectResourceKey(message),
            Path = path ?? filePath,
            ProviderId = providerId,
            Exception = exception
        };

    private static Uri CreateLocation(string filePath)
    {
        if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Scheme))
        {
            return uri;
        }

        return new Uri(Path.GetFullPath(filePath));
    }
}
