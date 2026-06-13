using MessagePack;

namespace Everywhere.StrategyEngine;

[MessagePackObject]
public sealed partial record PreprocessorResult
{
    /// <summary>
    /// Variables to inject into prompt interpolation.
    /// </summary>
    /// <remarks>
    /// Keys should match the placeholders used by strategy bodies, for example <c>selectedText</c> for <c>{selectedText}</c>.
    /// </remarks>
    [Key(0)] public IReadOnlyDictionary<string, string>? Variables { get; init; }
}
