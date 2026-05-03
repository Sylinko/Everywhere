namespace Everywhere.Chat.Plugins.BuiltIn.Terminal;

/// <summary>
/// Types of Shell Integration markers (OSC 633 sequences).
/// </summary>
internal enum ShellIntegrationMarkerType
{
    /// <summary>
    /// A — PromptStart: emitted at the beginning of the prompt function.
    /// </summary>
    PromptStart,

    /// <summary>
    /// B — CommandReady: emitted at the end of the prompt function, indicating the prompt is ready.
    /// </summary>
    CommandReady,

    /// <summary>
    /// C — CommandExecuted: emitted after the user's command has been sent to the shell for execution.
    /// </summary>
    CommandExecuted,

    /// <summary>
    /// D — CommandFinished: emitted at the next prompt, with optional exit code.
    /// </summary>
    CommandFinished,

    /// <summary>
    /// E — CommandLine: emitted with the actual command text before execution.
    /// </summary>
    CommandLine
}

/// <summary>
/// Represents a parsed Shell Integration marker from an OSC 633 sequence.
/// </summary>
/// <param name="Type">The marker type.</param>
/// <param name="ExitCode">Exit code (only for D markers).</param>
/// <param name="CommandLine">Command text (only for E markers).</param>
/// <param name="Line">The buffer line number where the marker was detected.</param>
internal readonly record struct ShellIntegrationMarker(
    ShellIntegrationMarkerType Type,
    int? ExitCode = null,
    string? CommandLine = null,
    int Line = -1);
