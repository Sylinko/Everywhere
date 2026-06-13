using Everywhere.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.StrategyEngine;

public sealed class RelativeFileStrategySourceResolver : IStrategySourceResolver
{
    public bool CanResolve(StrategyFromReference reference, StrategySource currentSource) =>
        !IsNonFileUri(reference.Source) &&
        !Path.IsPathRooted(reference.Source) &&
        currentSource.Location.IsFile;

    public Task<StrategyDocument> ResolveAsync(
        StrategyFromReference reference,
        StrategySource currentSource,
        CancellationToken cancellationToken)
    {
        var currentDirectory = Path.GetDirectoryName(currentSource.Location.LocalPath) ?? Directory.GetCurrentDirectory();
        var filePath = Path.GetFullPath(Path.Combine(currentDirectory, reference.Source));
        return StrategySourceDocumentFactory.LoadFileAsync(
            filePath,
            currentSource.ProviderId,
            reference.Kind,
            cancellationToken);
    }

    private static bool IsNonFileUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile;
}

public sealed class AbsoluteFileStrategySourceResolver : IStrategySourceResolver
{
    public bool CanResolve(StrategyFromReference reference, StrategySource currentSource) =>
        !IsNonFileUri(reference.Source) &&
        (Path.IsPathRooted(reference.Source) || Uri.TryCreate(reference.Source, UriKind.Absolute, out var uri) && uri.IsFile);

    public Task<StrategyDocument> ResolveAsync(
        StrategyFromReference reference,
        StrategySource currentSource,
        CancellationToken cancellationToken)
    {
        var filePath = Uri.TryCreate(reference.Source, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : reference.Source;
        return StrategySourceDocumentFactory.LoadFileAsync(
            Path.GetFullPath(filePath),
            currentSource.ProviderId,
            reference.Kind,
            cancellationToken);
    }

    private static bool IsNonFileUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile;
}

public sealed class UnsupportedUrlStrategySourceResolver : IStrategySourceResolver
{
    public bool CanResolve(StrategyFromReference reference, StrategySource currentSource) =>
        Uri.TryCreate(reference.Source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public Task<StrategyDocument> ResolveAsync(
        StrategyFromReference reference,
        StrategySource currentSource,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("URL strategy sources are not supported until a network policy is defined.");
}

public sealed class SkillStrategySourceResolver(IServiceProvider services) : IStrategySourceResolver
{
    public bool CanResolve(StrategyFromReference reference, StrategySource currentSource) =>
        reference.Source.StartsWith("skill://", StringComparison.OrdinalIgnoreCase);

    public Task<StrategyDocument> ResolveAsync(
        StrategyFromReference reference,
        StrategySource currentSource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var skillManager = services.GetService<ISkillManager>() ??
            throw new NotSupportedException("Skill source resolution requires ISkillManager.");
        var result = skillManager.ResolveSkillReference(reference.Source);
        if (result.Skill is null)
        {
            throw new FileNotFoundException($"No installed skill matches '{reference.Source}'.");
        }

        var diagnostics = result.IsAmbiguous
            ? [CreateAmbiguousReferenceDiagnostic(reference, currentSource, result)]
            : Array.Empty<StrategyDiagnostic>();
        return Task.FromResult(StrategySourceDocumentFactory.CreateSkillDocument(
            result.Skill,
            currentSource.ProviderId,
            diagnostics: diagnostics));
    }

    private static StrategyDiagnostic CreateAmbiguousReferenceDiagnostic(
        StrategyFromReference reference,
        StrategySource currentSource,
        SkillResolutionResult result)
    {
        var selectedId = result.Skill?.Id ?? "unknown";
        var candidates = string.Join(", ", result.Candidates.Select(skill => skill.Id));
        return new StrategyDiagnostic
        {
            Severity = StrategyDiagnosticSeverity.Warning,
            Code = "strategy.ambiguous_skill_reference",
            MessageKey = new DirectResourceKey(
                $"Skill reference '{reference.Source}' matched multiple skills; selected '{selectedId}'. Candidates: {candidates}."),
            Path = "from",
            ProviderId = currentSource.ProviderId
        };
    }
}
