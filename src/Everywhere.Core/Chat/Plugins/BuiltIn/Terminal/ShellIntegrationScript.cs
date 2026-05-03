using Everywhere.Configuration;

namespace Everywhere.Chat.Plugins.BuiltIn.Terminal;

/// <summary>
/// Manages Shell Integration scripts deployed alongside the application.
/// Scripts are deployed via CopyToOutputDirectory to {AppContext.BaseDirectory}/Terminal/.
/// Wrapper files (containing nonce) are generated at runtime to the application data directory.
/// </summary>
internal static class ShellIntegrationScript
{
    /// <summary>
    /// Directory where shell integration scripts are deployed (alongside the application).
    /// </summary>
    private static string ScriptsDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");

    /// <summary>
    /// Directory where wrapper files (.zshrc, .bashrc with nonce) are generated at runtime.
    /// Uses the application's writable data folder to avoid temp directory / antivirus issues.
    /// </summary>
    private static string WrapperDirectory => RuntimeConstants.EnsureWritableDataFolderPath("plugins", "terminal");

    /// <summary>
    /// Returns the full path to the PowerShell shell integration script if it exists, null otherwise.
    /// </summary>
    public static string? EnsurePowerShellScript()
    {
        var path = Path.GetFullPath(Path.Combine(ScriptsDirectory, "shellIntegration.ps1"));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Returns the full path to the Zsh shell integration script if it exists, null otherwise.
    /// </summary>
    public static string? EnsureZshScript()
    {
        var path = Path.GetFullPath(Path.Combine(ScriptsDirectory, "shellIntegration.zsh"));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Returns the full path to the Bash shell integration script if it exists, null otherwise.
    /// </summary>
    public static string? EnsureBashScript()
    {
        var path = Path.GetFullPath(Path.Combine(ScriptsDirectory, "shellIntegration.bash"));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Builds the complete shell command-line arguments for the given shell type.
    /// Returns null if the required script file does not exist (caller should fall back to None strategy).
    /// </summary>
    /// <param name="shellType">The shell type.</param>
    /// <returns>Shell arguments array, or null if script not available.</returns>
    public static string[]? BuildShellArgs(ShellType shellType)
    {
        return shellType switch
        {
            ShellType.PowerShell => EnsurePowerShellScript() is { } psScript
                ? ["-NoProfile", "-NoLogo", "-NoExit",
                   "-Command", $"try {{ . '{psScript.Replace("'", "''")}' }} catch {{}}"]
                : null,

            ShellType.Zsh => EnsureZshScript() is not null
                ? [] // ZDOTDIR trick handled via environment variables
                : null,

            ShellType.Bash => EnsureBashScript() is not null
                ? ["--rcfile", Path.Combine(WrapperDirectory, ".bashrc")]
                : null,

            _ => throw new ArgumentOutOfRangeException(nameof(shellType), $"Unsupported shell type: {shellType}")
        };
    }

    /// <summary>
    /// Builds the environment variables needed for shell integration.
    /// Nonce is passed via environment variable (not hardcoded in scripts), matching VS Code's approach.
    /// </summary>
    /// <param name="shellType">The shell type.</param>
    /// <param name="nonce">A random nonce for marker verification.</param>
    /// <returns>Dictionary of environment variables to set.</returns>
    public static Dictionary<string, string> BuildEnvironmentVariables(ShellType shellType, string nonce)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NO_COLOR"] = "1",
            ["TERM"] = "xterm-256color",
            ["HISTFILE"] = "/dev/null"
        };

        switch (shellType)
        {
            case ShellType.PowerShell:
            {
                env["EVERYWHERE_NONCE"] = nonce;
                break;
            }
            case ShellType.Zsh:
            {
                env["EVERYWHERE_NONCE"] = nonce;
                // ZDOTDIR trick: point zsh to our wrapper dir so it sources our .zshrc
                // USER_ZDOTDIR preserves the original ZDOTDIR for the wrapper to restore
                env["USER_ZDOTDIR"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                env["ZDOTDIR"] = PrepareZshWrapper(nonce);
                break;
            }
            case ShellType.Bash:
            {
                env["EVERYWHERE_NONCE"] = nonce;
                PrepareBashWrapper(nonce);
                break;
            }
            default:
            {
                throw new ArgumentOutOfRangeException(nameof(shellType), $"Unsupported shell type: {shellType}");
            }
        }

        return env;
    }

    /// <summary>
    /// For Zsh, creates a wrapper .zshrc in the data directory that sources the integration script
    /// and then the user's original .zshrc. Returns the wrapper directory path (for ZDOTDIR).
    /// </summary>
    private static string PrepareZshWrapper(string nonce)
    {
        var wrapperDirectory = WrapperDirectory;
        var wrapperZshrc = Path.Combine(wrapperDirectory, ".zshrc");
        var scriptPath = EnsureZshScript();
        var userZshrc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zshrc");

        var content = $"""
            # Everywhere Shell Integration wrapper
            export EVERYWHERE_NONCE='{nonce}'
            source '{scriptPath}'
            """;

        if (File.Exists(userZshrc))
        {
            content += $"source '{userZshrc}'\n";
        }

        File.WriteAllText(wrapperZshrc, content);
        return wrapperDirectory;
    }

    /// <summary>
    /// For Bash, creates a wrapper .bashrc in the data directory that sources the integration script
    /// and then the user's original .bashrc.
    /// </summary>
    private static void PrepareBashWrapper(string nonce)
    {
        var wrapperDirectory = WrapperDirectory;
        var wrapperBashrc = Path.Combine(wrapperDirectory, ".bashrc");
        var scriptPath = EnsureBashScript();
        var userBashrc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bashrc");

        var content = $"""
            # Everywhere Shell Integration wrapper
            export EVERYWHERE_NONCE='{nonce}'
            source '{scriptPath}'
            """;

        if (File.Exists(userBashrc))
        {
            content += $"source '{userBashrc}'\n";
        }

        File.WriteAllText(wrapperBashrc, content);
    }
}
