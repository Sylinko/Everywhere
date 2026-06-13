using Microsoft.Extensions.Logging;

namespace Everywhere.StrategyEngine;

public sealed class UserStrategyProvider(
    IUserStrategySource source,
    IStrategyDefinitionNormalizer normalizer,
    IEnumerable<IStrategySourceResolver> sourceResolvers,
    ILogger<UserStrategyProvider> logger) : IStrategyProvider
{
    private const string ProviderNamespace = "user";

    private readonly Lock _lock = new();
    private readonly List<StrategyDiagnostic> _diagnostics = [];

    public string Namespace => ProviderNamespace;

    public IReadOnlyList<StrategyDiagnostic> Diagnostics
    {
        get
        {
            lock (_lock)
            {
                return _diagnostics.ToArray();
            }
        }
    }

    public IEnumerable<Strategy> GetStrategies()
    {
        var strategies = new List<Strategy>();
        var diagnostics = new List<StrategyDiagnostic>();

        foreach (var file in source.EnumerateStrategyFiles())
        {
            try
            {
                var result = LoadStrategy(file, CancellationToken.None).GetAwaiter().GetResult();
                diagnostics.AddRange(result.Diagnostics);
                if (result.Strategy is { } strategy)
                {
                    strategies.Add(EnsureUserStrategyIdentity(strategy));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load user strategy {Path}", file);
                diagnostics.Add(new StrategyDiagnostic
                {
                    Severity = StrategyDiagnosticSeverity.Error,
                    Code = "strategy.load_failed",
                    MessageKey = new DirectResourceKey($"Failed to load user strategy '{file}': {ex.Message}"),
                    Path = file,
                    ProviderId = ProviderNamespace,
                    Exception = ex
                });
            }
        }

        lock (_lock)
        {
            _diagnostics.Clear();
            _diagnostics.AddRange(diagnostics);
        }

        return strategies;
    }

    private async Task<StrategyNormalizationResult> LoadStrategy(string file, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(file, cancellationToken);
        var document = NormalizeUserStrategyDocumentIdentity(StrategyDocumentParser.Parse(file, content, ProviderNamespace), file);
        return await normalizer.NormalizeAsync(
            document,
            new StrategyLoadContext
            {
                SourceResolvers = sourceResolvers.ToArray()
            },
            cancellationToken);
    }

    private static StrategyDocument NormalizeUserStrategyDocumentIdentity(StrategyDocument document, string file)
    {
        if (document.Definition is not StrategyDefinitionV1 definition)
        {
            return document;
        }

        var folderName = new DirectoryInfo(Path.GetDirectoryName(file) ?? string.Empty).Name;
        var diagnostics = new List<StrategyDiagnostic>(document.Diagnostics);
        var name = definition.Name.NullIfWhiteSpace();
        var effectiveName = name;
        if (name is null)
        {
            diagnostics.Add(CreateDiagnostic(
                StrategyDiagnosticSeverity.Error,
                "strategy.missing_name",
                "User STRATEGY.md is missing required frontmatter field 'name'.",
                document.Source,
                "name"));
        }
        else
        {
            if (name.StartsWith($"{ProviderNamespace}.", StringComparison.Ordinal))
            {
                effectiveName = name[$"{ProviderNamespace}.".Length..].NullIfWhiteSpace() ?? name;
                diagnostics.Add(CreateDiagnostic(
                    StrategyDiagnosticSeverity.Warning,
                    "strategy.name_has_provider_prefix",
                    "User STRATEGY.md 'name' should be local and must not include the 'user.' provider prefix.",
                    document.Source,
                    "name"));
            }

            if (!name.Equals(folderName, StringComparison.Ordinal))
            {
                diagnostics.Add(CreateDiagnostic(
                    StrategyDiagnosticSeverity.Warning,
                    "strategy.name_folder_mismatch",
                    "User STRATEGY.md 'name' should match its containing folder name.",
                    document.Source,
                    "name"));
            }
        }

        return document with
        {
            Definition = definition with
            {
                Name = effectiveName
            },
            Diagnostics = diagnostics
        };
    }

    private static Strategy EnsureUserStrategyIdentity(Strategy strategy)
    {
        var id = strategy.Id.StartsWith($"{ProviderNamespace}.", StringComparison.Ordinal)
            ? strategy.Id
            : $"{ProviderNamespace}.{strategy.Id}";

        return strategy with
        {
            Id = id,
            Source = strategy.Source with { ProviderId = ProviderNamespace }
        };
    }

    private static StrategyDiagnostic CreateDiagnostic(
        StrategyDiagnosticSeverity severity,
        string code,
        string message,
        StrategySource source,
        string path) =>
        new()
        {
            Severity = severity,
            Code = code,
            MessageKey = new DirectResourceKey(message),
            Path = path,
            ProviderId = source.ProviderId
        };
}

file static class UserStrategyProviderStringExtensions
{
    public static string? NullIfWhiteSpace(this string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
