using Everywhere.Chat;
using Everywhere.StrategyEngine.ConditionExpression.PathBinding;

namespace Everywhere.StrategyEngine.ConditionExpression;

/// <summary>
/// Entry point that compiles raw strategy <c>when</c> values into executable strategy conditions.
/// </summary>
/// <remarks>
/// The compiler wires the three Condition DSL phases together: frontend normalization, semantic binding against
/// the public JSON-backed schema, and creation of the reusable <see cref="ConditionCompilation"/> artifact.
/// </remarks>
internal sealed class StrategyConditionCompiler(
    StrategyOptions options,
    ConditionExpressionBinder? binder = null,
    IEnumerable<IExtraContextProvider>? extraContextProviders = null
)
{
    private readonly ConditionExpressionBinder _binder = binder ?? CreateDefaultBinder();
    private readonly ExtraContextProviderPipeline? _extraContext = extraContextProviders is null
        ? null
        : new ExtraContextProviderPipeline(extraContextProviders, options);

    /// <summary>
    /// Compiles one strategy condition and appends any frontend or binding diagnostics to the supplied list.
    /// </summary>
    /// <remarks>
    /// A null return means the condition is not executable because at least one error diagnostic was produced.
    /// Warning diagnostics are preserved on the resulting condition and appear in explain output.
    /// </remarks>
    public IStrategyCondition? Compile(
        object when,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics)
    {
        var syntax = ConditionExpressionFrontend.Compile(when, source, diagnostics);
        if (syntax is null)
        {
            return null;
        }

        var bound = _binder.Bind(syntax, source, diagnostics);
        return diagnostics.Any(diagnostic => diagnostic.Severity == StrategyDiagnosticSeverity.Error) ?
            null :
            new ConditionExpressionCondition(
                when,
                new ConditionCompilation(syntax, bound, _binder.Roots, options, source, _extraContext),
                source);
    }

    private static ConditionExpressionBinder CreateDefaultBinder() =>
        new(
            [
                new StrategyPathRootProvider<StrategyAttachmentsRoot>(
                    "attachments",
                    context => new StrategyAttachmentsRoot(
                        context.Attachments.AsValueEnumerable().OfType<TextSelectionAttachment>().FirstOrDefault(a => a.IsPrimary) ??
                        context.Attachments.AsValueEnumerable().OfType<TextSelectionAttachment>().FirstOrDefault(),
                        context.Attachments.AsValueEnumerable().OfType<FileAttachment>().ToList(),
                        context.Attachments.AsValueEnumerable().OfType<TextAttachment>().ToList())),
                new StrategyPathRootProvider<ProcessInfo?>("activeProcess", context => context.ActiveProcess),
                new StrategyPathRootProvider<StrategyEnvironmentRoot>("environment", _ => StrategyEnvironmentRoot.Current),
                new StrategyPathRootProvider<ExtraContextSnapshot>("extra", _ => null, isDeferred: true)
            ],
            new JsonStrategyPathAccessorProvider());
}
