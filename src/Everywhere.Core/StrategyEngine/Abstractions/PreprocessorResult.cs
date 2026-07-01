using MessagePack;

namespace Everywhere.StrategyEngine;

[MessagePackObject]
public sealed partial record PreprocessorResult
{
    /// <summary>
    /// Key-value pairs to inject into interpolation engine.
    /// </summary>
    [Key(0)] public IReadOnlyDictionary<string, string>? Variables { get; init; }
}