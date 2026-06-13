namespace Everywhere.StrategyEngine;

public sealed class StrategyPreprocessorRegistry(IEnumerable<IStrategyPreprocessor> preprocessors)
    : IStrategyPreprocessorRegistry
{
    private readonly IReadOnlyDictionary<string, IStrategyPreprocessor> _preprocessors = preprocessors
        .GroupBy(preprocessor => preprocessor.Id, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

    public bool TryGet(string id, out IStrategyPreprocessor preprocessor) =>
        _preprocessors.TryGetValue(id, out preprocessor!);

    public IReadOnlyList<IStrategyPreprocessor> GetAll() => _preprocessors.Values.ToArray();
}
