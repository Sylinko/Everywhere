using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using DynamicData;
using Everywhere.Chat.Permissions;
using Everywhere.Common;
using Everywhere.Interop;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Porta.Pty;
using ZLinq;

namespace Everywhere.Chat.Plugins.BuiltIn;

/// <summary>
/// A unified cross-platform terminal plugin that uses PTY (pseudo-terminal) for shell command execution.
/// Replaces the platform-specific PowerShellPlugin, BashPlugin, and ZshPlugin.
/// </summary>
public sealed partial class TerminalPlugin : BuiltInChatPlugin
{
    public override IDynamicResourceKey HeaderKey { get; } = new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_Terminal_Header);

    public override IDynamicResourceKey DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_Terminal_Description);

    public override LucideIconKind? Icon => LucideIconKind.SquareTerminal;

    private readonly IWatchdogManager _watchdogManager;
    private readonly ILogger<TerminalPlugin> _logger;

    /// <summary>
    /// Cached shell executable path to avoid repeated filesystem lookups.
    /// </summary>
    private string? _cachedShellApp;
    private string[]? _cachedShellArgs;

    public TerminalPlugin(IWatchdogManager watchdogManager, ILogger<TerminalPlugin> logger) : base("terminal")
    {
        _watchdogManager = watchdogManager;
        _logger = logger;

        _functionsSource.Add(
            new BuiltInChatFunction(
                ExecuteInTerminalAsync,
                ChatFunctionPermissions.ShellExecute,
                isAutoApproveAllowed: false,
                onPermissionConsent: _ => true));
    }

    [KernelFunction("execute_in_terminal")]
#if WINDOWS
    [Description("Executes script in PowerShell and obtains its output.")]
#elif MACOS
    [Description("Executes script in zsh and obtains its output.")]
#else
    [Description("Executes script in bash and obtains its output.")]
#endif
    [DynamicResourceKey(
        LocaleKey.BuiltInChatPlugin_Terminal_ExecuteScript_Header,
        LocaleKey.BuiltInChatPlugin_Terminal_ExecuteScript_Description)]
    private async Task<string> ExecuteInTerminalAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatContextManager chatContextManager,
        [FromKernelServices] ChatContext chatContext,
        [Description("A concise description for user, explaining what are you doing")] string description,
        [Description("Single or multi-line shell script")] string script,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Executing shell script with description: {Description}", description);

        if (string.IsNullOrWhiteSpace(script))
        {
            throw new ArgumentException("Script cannot be null or empty.", nameof(script));
        }

        // Consent
        string? consentKey;
        var trimmedScript = script.AsSpan().Trim();
        if (!trimmedScript.Contains('\n'))
        {
            var command = trimmedScript[trimmedScript.Split(' ').FirstOrDefault(new Range(0, trimmedScript.Length))].ToString();
            consentKey = $"single.{command}";
        }
        else
        {
            consentKey = "multi-line";
        }

        var detailBlock = new ChatPluginContainerDisplayBlock
        {
            new ChatPluginTextDisplayBlock(description),
            new ChatPluginCodeBlockDisplayBlock(script, DetectLanguageHint()),
        };

        var consent = await userInterface.RequestConsentAsync(
            consentKey,
            new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_Terminal_ExecuteScript_ScriptConsent_Header),
            detailBlock,
            canRemember: false,
            cancellationToken: cancellationToken);
        if (!consent)
        {
            throw new HandledException(
                new UnauthorizedAccessException(consent.FormatReason("User denied consent for shell script execution.")),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_Terminal_ExecuteScript_DenyMessage),
                showDetails: false);
        }

        userInterface.DisplaySink.AppendBlocks(detailBlock);

        // Detect shell
        var (shellApp, shellArgs) = DetectShell();
        var workingDirectory = chatContextManager.EnsureWorkingDirectory(chatContext);

        // Build environment
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NO_COLOR"] = "1",
        };

        if (!OperatingSystem.IsWindows())
        {
            environment["TERM"] = "dumb";
        }

        if (EnvironmentVariableUtilities.GetLatestPathVariable() is { Length: > 0 } latestPath)
        {
            environment["PATH"] = latestPath;
        }

        // Build preamble + script + sentinel
        var sentinel = $"___EOF___{Guid.NewGuid():N}___";
        var preamble = BuildPreamble();
        var fullScript = new StringBuilder();
        if (preamble is not null)
        {
            fullScript.AppendLine(preamble);
        }
        fullScript.Append(script);
        fullScript.AppendLine();
        fullScript.AppendLine($"echo \"{sentinel}\"");
        fullScript.AppendLine("exit");

        // Spawn PTY
        var options = new PtyOptions
        {
            Name = "Everywhere-Terminal",
            Cols = 200,
            Rows = 50,
            Cwd = workingDirectory,
            App = shellApp,
            CommandLine = shellArgs,
            VerbatimCommandLine = true,
            Environment = environment,
        };

        string result;
        using (var pty = await PtyProvider.SpawnAsync(options, cancellationToken))
        {
            var pid = pty.Pid;

            // Register with Watchdog
            await _watchdogManager.RegisterProcessAsync(pid);

            try
            {
                // Write script to PTY stdin
                var scriptBytes = Encoding.UTF8.GetBytes(fullScript.ToString());
                await pty.WriterStream.WriteAsync(scriptBytes, cancellationToken);
                await pty.WriterStream.FlushAsync(cancellationToken);

                // Read output with sentinel detection and timeout
                result = await ReadPtyOutputAsync(pty, sentinel, TimeSpan.FromSeconds(30), cancellationToken);

                // Wait for process to exit
                pty.WaitForExit(5000);
            }
            finally
            {
                // Unregister from Watchdog
                await _watchdogManager.UnregisterProcessAsync(pid, killIfRunning: false);
            }
        }

        // Clean output
        result = CleanPtyOutput(result, sentinel, preamble);

        userInterface.DisplaySink.AppendCodeBlock(result.Trim(), "log");
        return result;
    }

    /// <summary>
    /// Detect the platform shell and build the command line.
    /// </summary>
    private (string App, string[] Args) DetectShell()
    {
        if (_cachedShellApp is not null && _cachedShellArgs is not null)
        {
            return (_cachedShellApp, _cachedShellArgs);
        }

        string app;
        string[] args;

        if (OperatingSystem.IsWindows())
        {
            // Prefer pwsh (7+) over Windows PowerShell (5.1)
            app = FindPowerShellExecutable() ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            args = ["-NoProfile", "-NoLogo"];
        }
        else if (OperatingSystem.IsMacOS())
        {
            app = "/bin/zsh";
            args = [];
        }
        else // Linux
        {
            app = "/bin/bash";
            args = [];
        }

        _cachedShellApp = app;
        _cachedShellArgs = args;
        return (app, args);
    }

    /// <summary>
    /// Build a preamble command to configure the shell environment.
    /// Returns null if no preamble is needed.
    /// </summary>
    private static string? BuildPreamble()
    {
        if (OperatingSystem.IsWindows())
        {
            // Disable ANSI colors and set UTF-8 encoding for PowerShell
            return "$PSStyle.OutputRendering = 'PlainText'; [Console]::OutputEncoding = [System.Text.Encoding]::UTF8";
        }

        // Unix: no preamble needed, TERM=dumb and NO_COLOR=1 are set via environment
        return null;
    }

    /// <summary>
    /// Detect the language hint for code block display.
    /// </summary>
    private static string DetectLanguageHint()
    {
        if (OperatingSystem.IsWindows()) return "powershell";
        if (OperatingSystem.IsMacOS()) return "zsh";
        return "bash";
    }

    /// <summary>
    /// Read PTY output until the sentinel is found or timeout occurs.
    /// </summary>
    private async Task<string> ReadPtyOutputAsync(
        IPtyConnection pty,
        string sentinel,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var output = new StringBuilder();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            while (!linkedToken.IsCancellationRequested)
            {
                var bytesRead = await pty.ReaderStream.ReadAsync(buffer, linkedToken);
                if (bytesRead == 0) break; // Stream closed (process exited)

                output.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

                // Check if sentinel is in the output
                if (output.ToString().Contains(sentinel))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning("PTY output read timed out after {Timeout}", timeout);
            // Try to kill the process on timeout
            try { pty.Kill(); }
            catch
            { /* ignore */
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Clean PTY output by removing ANSI escape sequences, collapsing \r, and extracting content before sentinel.
    /// </summary>
    private static string CleanPtyOutput(string rawOutput, string sentinel, string? preamble)
    {
        // 1. Extract content before sentinel
        var sentinelIndex = rawOutput.IndexOf(sentinel, StringComparison.Ordinal);
        if (sentinelIndex >= 0)
        {
            rawOutput = rawOutput[..sentinelIndex];
        }

        // 2. Strip ANSI escape sequences
        rawOutput = AnsiEscapeRegex().Replace(rawOutput, string.Empty);

        // 3. Collapse \r sequences (progress bars → keep final state)
        var lines = rawOutput.Split('\n');
        var cleanedLines = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            // For each line, split by \r and take the last segment (final state)
            var segments = line.Split('\r');
            var lastSegment = segments[^1].TrimEnd();
            if (lastSegment.Length > 0)
            {
                cleanedLines.Add(lastSegment);
            }
        }

        // 4. Remove preamble echo lines
        if (preamble is not null)
        {
            // The preamble command itself will be echoed back. Remove lines that match.
            // Also remove the prompt line (e.g., "PS C:\...>" or "user@host:~$ ")
            var preambleLines = preamble.Split(';', StringSplitOptions.TrimEntries);
            cleanedLines.RemoveAll(line =>
                preambleLines.Any(p => line.Contains(p, StringComparison.OrdinalIgnoreCase)));
        }

        // 5. Remove common shell prompt patterns at the start
        // PowerShell: "PS C:\path>" or "PS>"
        // Bash/Zsh: "user@host:~$" or "$ "
        // These appear at the beginning of the output
        while (cleanedLines.Count > 0)
        {
            var first = cleanedLines[0];
            if (IsPromptLine(first))
            {
                cleanedLines.RemoveAt(0);
            }
            else
            {
                break;
            }
        }

        // Remove trailing prompt
        while (cleanedLines.Count > 0)
        {
            var last = cleanedLines[^1];
            if (IsPromptLine(last))
            {
                cleanedLines.RemoveAt(cleanedLines.Count - 1);
            }
            else
            {
                break;
            }
        }

        return string.Join('\n', cleanedLines).Trim();
    }

    /// <summary>
    /// Check if a line looks like a shell prompt that should be stripped.
    /// </summary>
    private static bool IsPromptLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        // PowerShell prompt: "PS C:\path>" or "PS>"
        if (line.StartsWith("PS ", StringComparison.Ordinal) && line.Contains('>'))
            return true;

        // Generic prompt patterns ending with $ or >
        // e.g., "user@host:~$" or "bash-5.1$"
        if (line.EndsWith('$') || (line.EndsWith('>') && line.Length < 100))
            return true;

        return false;
    }

    /// <summary>
    /// Regex to match ANSI escape sequences.
    /// </summary>
    [GeneratedRegex(@"\x1b\[[0-9;]*[a-zA-Z]|\x1b\][^\x07]*\x07|\x1b[()][AB012]")]
    private static partial Regex AnsiEscapeRegex();

    #region Shell Detection (Windows)

    private static string? FindPowerShellExecutable()
    {
        // 1. Use PATH first
        var pwshInPath = FindInPath("pwsh.exe");
        if (!string.IsNullOrEmpty(pwshInPath)) return pwshInPath;

        // 2. Search in Program Files
        var bestProgramFilesVersion = FindBestVersionInProgramFiles();
        if (!string.IsNullOrEmpty(bestProgramFilesVersion)) return bestProgramFilesVersion;

        // 3. Fallback to legacy Windows PowerShell
        var legacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        if (File.Exists(legacyPath)) return legacyPath;

        return null;
    }

    private static string? FindBestVersionInProgramFiles()
    {
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        var foundExecutables = new List<(Version Version, string? Path)>();
        foreach (var psRoot in roots
                     .AsValueEnumerable()
                     .Where(root => !string.IsNullOrEmpty(root))
                     .Select(root => Path.Combine(root, "PowerShell"))
                     .Where(Directory.Exists))
        {
            try
            {
                var dirs = Directory.GetDirectories(psRoot);

                foreach (var dir in dirs)
                {
                    var exePath = Path.Combine(dir, "pwsh.exe");
                    if (!File.Exists(exePath)) continue;

                    var folderName = Path.GetFileName(dir);
                    var match = VersionRegex().Match(folderName);
                    if (match.Success && Version.TryParse(match.Value, out var v))
                    {
                        foundExecutables.Add((v, exePath));
                    }
                }
            }
            catch
            {
                // Ignore
            }
        }

        return foundExecutables.AsValueEnumerable().OrderByDescending(x => x.Version).FirstOrDefault().Path;
    }

    private static string? FindInPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in paths)
        {
            try
            {
                var fullPath = Path.Combine(path.Trim(), fileName);
                if (File.Exists(fullPath)) return fullPath;
            }
            catch
            {
                // Ignore
            }
        }

        return null;
    }

    [GeneratedRegex(@"^(\d+(\.\d+)*)")]
    private static partial Regex VersionRegex();

    #endregion

}