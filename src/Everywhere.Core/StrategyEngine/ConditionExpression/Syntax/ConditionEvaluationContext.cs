namespace Everywhere.StrategyEngine.ConditionExpression.Syntax;

internal sealed class ConditionEvaluationContext(
    StrategyContext strategyContext,
    StrategyOptions options,
    StrategySource source,
    List<StrategyDiagnostic> diagnostics,
    ConditionPathResolver pathResolver,
    object? itemScope = null)
{
    public StrategyContext StrategyContext { get; } = strategyContext;

    public StrategyOptions Options { get; } = options;

    public StrategySource Source { get; } = source;

    public List<StrategyDiagnostic> Diagnostics { get; } = diagnostics;

    public ConditionPathResolver PathResolver { get; } = pathResolver;

    public object? ItemScope { get; } = itemScope;

    public ConditionEvaluationContext WithItemScope(object? item) =>
        new(StrategyContext, Options, Source, Diagnostics, PathResolver, item);

    public void AddDiagnostic(
        StrategyDiagnosticSeverity severity,
        string code,
        string message,
        string path,
        Exception? exception = null,
        TimeSpan? duration = null,
        string? providerId = null) =>
        Diagnostics.Add(new StrategyDiagnostic
        {
            Severity = severity,
            Code = code,
            MessageKey = new DirectResourceKey(message),
            Path = path,
            ProviderId = providerId ?? Source.ProviderId,
            Exception = exception,
            Duration = duration
        });
}

internal static class ConditionLogicalSemantics
{
    public static bool? EvaluateAll(IReadOnlyList<ConditionNode> children, ConditionEvaluationContext context)
    {
        var hasNull = false;
        foreach (var child in children.AsValueEnumerable())
        {
            var value = child.Evaluate(context);
            if (value is false)
            {
                return false;
            }

            hasNull |= value is null;
        }

        return hasNull ? null : true;
    }

    public static bool? EvaluateAny(IReadOnlyList<ConditionNode> children, ConditionEvaluationContext context)
    {
        var hasNull = false;
        foreach (var child in children.AsValueEnumerable())
        {
            var value = child.Evaluate(context);
            if (value is true)
            {
                return true;
            }

            hasNull |= value is null;
        }

        return hasNull ? null : false;
    }

    public static bool? Invert(bool? value) =>
        value switch
        {
            true => false,
            false => true,
            _ => null
        };
}
