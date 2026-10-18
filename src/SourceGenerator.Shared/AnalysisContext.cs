using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Everywhere.SourceGeneration;

/// <summary>
/// Collects diagnostics during a single semantic transform. It never escapes into a cached model.
/// </summary>
internal sealed class AnalysisContext(CancellationToken cancellationToken)
{
    /// <summary>
    /// Gets cancellation for the current semantic analysis.
    /// </summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;

    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

    /// <summary>
    /// Records a diagnostic with its original source location and suppression semantics.
    /// </summary>
    public void ReportDiagnostic(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    /// <summary>
    /// Separates the value model from diagnostics, whose positions may change independently.
    /// </summary>
    public AnalysisResult<T> Complete<T>(T? model) where T : class => new(model, _diagnostics.ToImmutable());
}

/// <summary>
/// Carries the two outputs of semantic analysis until source and diagnostic pipelines split.
/// </summary>
internal sealed class AnalysisResult<T>(T? model, ImmutableArray<Diagnostic> diagnostics) where T : class
{
    /// <summary>
    /// Gets the symbol-free source model, if the declaration can generate source.
    /// </summary>
    public T? Model { get; } = model;

    /// <summary>
    /// Gets current diagnostics, retaining source locations only on the diagnostic branch.
    /// </summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; } = diagnostics;
}
