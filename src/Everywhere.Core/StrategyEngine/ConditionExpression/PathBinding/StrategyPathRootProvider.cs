namespace Everywhere.StrategyEngine.ConditionExpression.PathBinding;

/// <summary>
/// Simple root provider backed by a delegate.
/// </summary>
/// <example>
/// <c>new StrategyPathRootProvider&lt;ProcessInfo?&gt;("activeProcess", context =&gt; context.ActiveProcess)</c>
/// </example>
internal sealed class StrategyPathRootProvider<T>(
    string rootName,
    Func<StrategyContext, object?> getValue,
    bool isDeferred = false) : IStrategyPathRootProvider
{
    public string RootName { get; } = rootName;

    public Type ValueType => typeof(T);

    public bool IsDeferred { get; } = isDeferred;

    public object? GetValue(StrategyContext context) => getValue(context);
}
