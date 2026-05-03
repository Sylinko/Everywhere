namespace Everywhere.Chat.Plugins.BuiltIn.Terminal;

/// <summary>
/// Identifies the shell type detected for the current platform.
/// </summary>
internal enum ShellType
{
    /// <summary>
    /// Unsupported or unrecognized shell.
    /// </summary>
    Unknown,

    /// <summary>
    /// PowerShell (pwsh 7+ or Windows PowerShell 5.1).
    /// </summary>
    PowerShell,

    /// <summary>
    /// Zsh (default on macOS).
    /// </summary>
    Zsh,

    /// <summary>
    /// Bash (default on Linux).
    /// </summary>
    Bash
}
