using System.Text;
using Everywhere.Common;

namespace Everywhere.Terminal;

/// <summary>
/// Manages Shell Integration scripts deployed alongside the application.
/// Scripts are deployed via CopyToOutputDirectory to {AppContext.BaseDirectory}/Terminal/.
/// Wrapper files (containing nonce) are generated at runtime to the application data directory.
/// </summary>
public static class ShellIntegrationScript
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
    /// Returns a normalized shell identifier (<c>pwsh</c>, <c>powershell</c>, <c>zsh</c>, <c>bash</c>, or <c>unknown</c>).
    /// </summary>
    public static string GetShellKind(ShellType shellType, string? shellPath = null) => shellType switch
    {
        ShellType.PowerShell => Path.GetFileNameWithoutExtension(shellPath)?.Equals("pwsh", StringComparison.OrdinalIgnoreCase) is true
            ? "pwsh"
            : "powershell",
        ShellType.Zsh => "zsh",
        ShellType.Bash => "bash",
        _ => "unknown"
    };

    /// <summary>
    /// Builds the complete shell command-line arguments for the given shell type.
    /// Returns null if the required script file does not exist (caller should fall back to None strategy).
    /// </summary>
    /// <param name="shellType">The shell type.</param>
    /// <param name="shellPath">Optional shell executable path used to distinguish <c>pwsh</c> from Windows PowerShell 5.1.</param>
    /// <returns>Shell arguments array, or null if script not available.</returns>
    public static string[]? BuildShellArgs(ShellType shellType, string? shellPath = null)
    {
        switch (shellType)
        {
            case ShellType.PowerShell:
            {
                if (EnsurePowerShellScript() is not { } psScript) return null;

                var command =
                    $"try {{ . '{psScript.Replace("'", "''")}' }} catch {{ [Console]::Write([string][char]0x1b + ']633;P;IntegrationError=' + $_.Exception.GetType().FullName + [char]7) }}";

                // Windows PowerShell 5.1 defaults to Restricted execution policy on client SKUs; align with pwsh's RemoteSigned.
                if (OperatingSystem.IsWindows() && GetShellKind(shellType, shellPath) == "powershell")
                {
                    return ["-ExecutionPolicy", "RemoteSigned", "-NoProfile", "-NoLogo", "-NoExit", "-Command", command];
                }

                return ["-NoProfile", "-NoLogo", "-NoExit", "-Command", command];
            }
            case ShellType.Zsh:
                return EnsureZshScript() is not null ? [] : null; // ZDOTDIR trick handled via environment variables
            case ShellType.Bash:
                return EnsureBashScript() is not null ? ["--rcfile", Path.Combine(WrapperDirectory, ".bashrc")] : null;
            default:
                throw new ArgumentOutOfRangeException(nameof(shellType), $"Unsupported shell type: {shellType}");
        }
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
            ["HISTFILE"] = "/dev/null",
            ["HISTSIZE"] = "0",
            ["HISTFILESIZE"] = "0",
            ["SAVEHIST"] = "0"
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
                env["ZDOTDIR"] = PrepareZshWrapper();
                break;
            }
            case ShellType.Bash:
            {
                env["EVERYWHERE_NONCE"] = nonce;
                PrepareBashWrapper();
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
    /// For Zsh, creates a wrapper .zshrc in the data directory that restores ZDOTDIR, sources the user's
    /// original .zshrc, and then sources the integration script so user prompt frameworks cannot clobber hooks.
    /// Returns the wrapper directory path (for ZDOTDIR).
    /// </summary>
    private static string PrepareZshWrapper()
    {
        var wrapperDirectory = WrapperDirectory;
        var wrapperZshrc = Path.Combine(wrapperDirectory, ".zshrc");
        var scriptPath = EnsureZshScript();
        var userZshrc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zshrc");

        var contentBuilder = new StringBuilder()
            .Append("# Everywhere Shell Integration wrapper\n")
            .Append("if [ -n \"${USER_ZDOTDIR:-}\" ]; then ZDOTDIR=\"$USER_ZDOTDIR\"; fi\n");

        if (File.Exists(userZshrc))
        {
            contentBuilder.Append("source ").Append(QuotePosix(userZshrc)).Append('\n');
        }

        if (scriptPath is not null)
        {
            contentBuilder.Append("source ").Append(QuotePosix(scriptPath)).Append('\n');
        }

        WriteWrapperIfChanged(wrapperZshrc, contentBuilder.ToString());
        return wrapperDirectory;
    }

    /// <summary>
    /// For Bash, creates a wrapper .bashrc in the data directory that sources the user's original .bashrc
    /// and then sources the integration script so user prompt frameworks cannot clobber hooks.
    /// </summary>
    private static void PrepareBashWrapper()
    {
        var wrapperDirectory = WrapperDirectory;
        var wrapperBashrc = Path.Combine(wrapperDirectory, ".bashrc");
        var scriptPath = EnsureBashScript();
        var userBashrc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bashrc");

        var contentBuilder = new StringBuilder("# Everywhere Shell Integration wrapper\n");

        if (File.Exists(userBashrc))
        {
            contentBuilder.Append("if [ -r ").Append(QuotePosix(userBashrc)).Append(" ]; then . ").Append(QuotePosix(userBashrc)).Append("; fi\n");
        }

        if (scriptPath is not null)
        {
            contentBuilder.Append("source ").Append(QuotePosix(scriptPath)).Append('\n');
        }

        WriteWrapperIfChanged(wrapperBashrc, contentBuilder.ToString());
    }

    private static string QuotePosix(string path) => "'" + path.Replace("'", "'\\''") + "'";

    private static void WriteWrapperIfChanged(string path, string content)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == content)
            {
                return;
            }
        }
        catch (IOException)
        {
            // Fall through to update the wrapper atomically.
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content, new UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another concurrent invocation updated the shared wrapper file.
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Ignore cleanup failure for temporary file.
            }
        }
    }
}
