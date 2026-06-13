using MessagePack;

namespace Everywhere.StrategyEngine;

[MessagePackObject]
public sealed partial record PreprocessorResult
{
    /// <summary>
    /// Variables to inject into prompt interpolation.
    /// </summary>
    /// <remarks>
    /// New preprocessors should use path-style keys, for example <c>preprocess.selection.text</c> for
    /// <c>{preprocess.selection.text}</c>. Older aliases can still be produced during migration.
    /// </remarks>
    [Key(0)] public IReadOnlyDictionary<string, string>? Variables { get; init; }

    /// <summary>
    /// Diagnostics produced by the preprocessor itself.
    /// </summary>
    /// <remarks>
    /// Diagnostics are execution-time data and are intentionally not persisted with chat history yet. The merged
    /// variable values are persisted so retries/replays stay stable.
    /// </remarks>
    [IgnoreMember] public IReadOnlyList<StrategyDiagnostic> Diagnostics { get; init; } = [];
}
