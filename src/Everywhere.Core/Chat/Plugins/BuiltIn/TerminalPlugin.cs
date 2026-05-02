using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using DynamicData;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins.BuiltIn.Terminal;
using Everywhere.Common;
using Everywhere.Configuration;
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

    public override IReadOnlyList<SettingsItem> SettingsItems => _pluginSettings.SettingsItems;

    private readonly TerminalPluginSettings _pluginSettings;
    private readonly IWatchdogManager _watchdogManager;
    private readonly ILogger<TerminalPlugin> _logger;

    public TerminalPlugin(Settings settings, IWatchdogManager watchdogManager, ILogger<TerminalPlugin> logger) : base("terminal")
    {
        _pluginSettings = settings.Plugin.Terminal;

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

        // Detect shell
        var (shellPath, shellArgs) = DetectShell();

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
            new ChatPluginCodeBlockDisplayBlock(script, DetectLanguageHint(shellPath)),
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

        // Build script to send via stdin
        // The shell will exit after processing stdin (EOF closes the stream)
        var fullScript = new StringBuilder();
        fullScript.Append(script);
        fullScript.AppendLine();
        fullScript.AppendLine("exit");

        // Spawn PTY
        var options = new PtyOptions
        {
            Name = "Everywhere-Terminal",
            Cols = 1024, // Wide columns to prevent soft-wrap that would break command echo detection
            Rows = 50,
            Cwd = workingDirectory,
            App = shellPath,
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
                pty.WriterStream.Close(); // Signal EOF to the shell

                // Capture output through VT buffer until process exits
                result = await CaptureTerminalOutputAsync(pty, TimeSpan.FromSeconds(30), cancellationToken);

                // Wait for process to exit
                pty.WaitForExit(5000);
            }
            finally
            {
                // Unregister from Watchdog
                await _watchdogManager.UnregisterProcessAsync(pid, killIfRunning: false);
            }
        }

        // Clean output: strip command echo and trailing prompt
        result = CleanPtyOutput(result, script);

        userInterface.DisplaySink.AppendCodeBlock(result.Trim(), "log");
        return result;
    }

    /// <summary>
    /// Detect the platform shell and build the command line.
    /// </summary>
    private (string ShellPath, string[] Args) DetectShell()
    {
        if (!_pluginSettings.ShellPath.IsNullOrWhiteSpace())
        {
            return (_pluginSettings.ShellPath, SplitCommandLineArguments(_pluginSettings.ShellArgs));
        }

        string shellPath;
        string? shellArgs;

        if (OperatingSystem.IsWindows())
        {
            // Prefer pwsh (7+) over Windows PowerShell (5.1)
            shellPath = FindPowerShellExecutable() ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            shellArgs = "-NoProfile -NoLogo";
        }
        else if (OperatingSystem.IsMacOS())
        {
            shellPath = "/bin/zsh";
            shellArgs = null;
        }
        else // Linux
        {
            shellPath = "/bin/bash";
            shellArgs = null;
        }

        _pluginSettings.ShellPath = shellPath;
        _pluginSettings.ShellArgs = shellArgs;
        return (shellPath, SplitCommandLineArguments(shellArgs));
    }

    /// <summary>
    /// Splits a command-line argument string into an array, respecting quoted strings.
    /// Supports both double quotes (Windows) and single quotes (Unix).
    /// </summary>
    private static string[] SplitCommandLineArguments(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return [];

        var result = new List<string>();
        var current = new StringBuilder();
        var inDoubleQuote = false;
        var inSingleQuote = false;
        var i = 0;

        while (i < args.Length)
        {
            var c = args[i];

            if (inDoubleQuote)
            {
                switch (c)
                {
                    case '"' when (i + 1 >= args.Length || args[i + 1] != '"'):
                    {
                        inDoubleQuote = false;
                        break;
                    }
                    case '"' when i + 1 < args.Length && args[i + 1] == '"':
                    {
                        // Escaped double quote inside double-quoted string: "" → "
                        current.Append('"');
                        i++;
                        break;
                    }
                    default:
                    {
                        current.Append(c);
                        break;
                    }
                }
            }
            else if (inSingleQuote)
            {
                if (c == '\'')
                {
                    inSingleQuote = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                switch (c)
                {
                    case '"':
                    {
                        inDoubleQuote = true;
                        break;
                    }
                    case '\'':
                    {
                        inSingleQuote = true;
                        break;
                    }
                    case '\\' when i + 1 < args.Length:
                    {
                        // Backslash escape: consume next char literally
                        current.Append(args[++i]);
                        break;
                    }
                    case ' ' or '\t':
                    {
                        if (current.Length > 0)
                        {
                            result.Add(current.ToString());
                            current.Clear();
                        }
                        break;
                    }
                    default:
                    {
                        current.Append(c);
                        break;
                    }
                }
            }

            i++;
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result.ToArray();
    }

    /// <summary>
    /// Detect the language hint for code block display.
    /// </summary>
    private static string DetectLanguageHint(string? shellPath)
    {
        return Path.GetFileNameWithoutExtension(shellPath)?.ToLowerInvariant() switch
        {
            "powershell" or "pwsh" => "powershell",
            "sh" => "sh",
            "zsh" => "zsh",
            "bash" => "bash",
            _ => "shell"
        };
    }

    /// <summary>
    /// Capture PTY output through a virtual terminal buffer until the process exits or timeout.
    /// The VT buffer handles ANSI sequences, cursor movement, erase operations, etc.,
    /// producing clean text from the final terminal state.
    /// </summary>
    private async Task<string> CaptureTerminalOutputAsync(
        IPtyConnection pty,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var vtBuffer = new VirtualTerminalBuffer(1024);
        var parser = new VtSequenceParser(vtBuffer);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            while (!linkedToken.IsCancellationRequested)
            {
                var bytesRead = await pty.ReaderStream.ReadAsync(buffer, linkedToken);
                if (bytesRead == 0) break; // Stream closed (process exited)

                var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                parser.Feed(text);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning("PTY output read timed out after {Timeout}", timeout);
            try { pty.Kill(); }
            catch { /* ignore */ }
        }

        return vtBuffer.GetText();
    }

    /// <summary>
    /// Clean PTY output from the virtual terminal buffer.
    /// The VT buffer already handled ANSI sequences, cursor movement, and \r/\n.
    /// This method only does text-level cleaning: strip command echo and trailing prompt.
    /// </summary>
    private static string CleanPtyOutput(string rawOutput, string script)
    {
        // Strip script echo + trailing prompt
        rawOutput = StripCommandEchoAndPrompt(rawOutput, script);

        // Truncate if too long (keep tail)
        // TODO: elevate trimming logic to ChatService
        const int maxLength = 60_000;
        if (rawOutput.Length > maxLength)
        {
            const string truncationMessage = "\n\n[... PREVIOUS OUTPUT TRUNCATED ...]\n\n";
            var availableLength = maxLength - truncationMessage.Length;
            rawOutput = truncationMessage + rawOutput[^availableLength..];
        }

        return rawOutput.Trim();
    }

    #region VS Code-inspired output cleaning

    /// <summary>
    /// Strips the command echo and trailing prompt lines from terminal output.
    /// Ported from VS Code's strategyHelpers.ts stripCommandEchoAndPrompt.
    ///
    /// Without shell integration, PTY output captures:
    /// 1. The command echo line (what was sent via stdin, with prompt prefix)
    /// 2. The actual command output
    /// 3. The next shell prompt line(s)
    ///
    /// This function removes (1) and (3) to isolate the actual output.
    /// </summary>
    private static string StripCommandEchoAndPrompt(string output, string commandLine)
    {
        var result = StripCommandEchoAndPromptOnce(output, commandLine);

        // After stripping the first command echo and trailing prompt, the remaining
        // content may still contain the command re-echoed by the shell. If the command
        // appears again in the remaining text, strip it one more time.
        if (result.Trim().Length > 0 && FindCommandEcho(result, commandLine, allowSuffixMatch: false).HasValue)
        {
            result = StripCommandEchoAndPromptOnce(result, commandLine);
        }

        return result;
    }

    /// <summary>
    /// Single-pass strip of command echo and trailing prompt.
    /// </summary>
    /// <remarks>
    /// https://github.com/microsoft/vscode/blob/161a11c5/src/vs/workbench/contrib/terminalContrib/chatAgentTools/browser/executeStrategy/strategyHelpers.ts
    /// </remarks>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="RegexMatchTimeoutException"></exception>
    private static string StripCommandEchoAndPromptOnce(string output, string commandLine)
    {
        // Strip leading lines that are part of the command echo
        var echoResult = FindCommandEcho(output, commandLine, allowSuffixMatch: true);
        string[] lines;
        const int startIndex = 0;

        // Use evidence from the prompt prefix to narrow down which trailing prompt patterns to check
        var promptBefore = echoResult?.ContentBefore ?? "";
        var isUnixAt = UnixAtRegex().IsMatch(promptBefore);
        var isUnixHost = !isUnixAt && UnixHostRegex().IsMatch(promptBefore);
        var isUnix = isUnixAt || isUnixHost;
        var isPowerShell = PowerShellRegex().IsMatch(promptBefore);
        var isCmd = !isPowerShell && CmdRegex().IsMatch(promptBefore);
        var isStarship = promptBefore.Contains('\u276f');
        var isPython = promptBefore.Contains(">>>");
        var knownPrompt = isUnix || isPowerShell || isCmd || isStarship || isPython;

        if (echoResult.HasValue)
        {
            lines = echoResult.Value.LinesAfter;
        }
        else
        {
            lines = output.Split('\n');
        }

        // Strip trailing lines that are part of the next shell prompt.
        // Prompts may span multiple lines due to terminal column wrapping.
        var endIndex = lines.Length;
        var trailingStrippedCount = 0;
        const int maxTrailingPromptLines = 2;

        while (endIndex > startIndex)
        {
            var line = lines[endIndex - 1].TrimEnd();
            if (line.Length == 0)
            {
                endIndex--;
                continue;
            }
            if (trailingStrippedCount >= maxTrailingPromptLines)
            {
                break;
            }

            // Complete (self-contained) prompt patterns
            var isCompletePrompt =
                // Bash/zsh: user@host:path ending with $ or #
                (!knownPrompt || isUnixAt) && BashZshPromptRegex().IsMatch(line) ||
                // hostname:path user$ or hostname:path user#
                (!knownPrompt || isUnixHost) && HostNamePathPromptRegex().IsMatch(line) ||
                // PowerShell: PS C:\path>
                (!knownPrompt || isPowerShell) && PowerShellPromptRegex().IsMatch(line) ||
                // Windows cmd: C:\path>
                (!knownPrompt || isCmd) && CmdPromptRegex().IsMatch(line) ||
                // Starship prompt character
                (!knownPrompt || isStarship) && line.EndsWith('\u276f') ||
                // Python REPL
                (!knownPrompt || isPython) && line.TrimEnd() == ">>>";

            // Fragment/partial prompt patterns (wrapped across terminal lines)
            var isPromptFragment =
                // Wrapped fragment ending with $ or # (e.g. "er$", "ts/testWorkspace$")
                (!knownPrompt || isUnix) && WrappedFragmentEndingPromptRegex().IsMatch(line) ||
                // Bracketed prompt start: [ hostname:/path or [ user@host:/path
                (!knownPrompt || isUnix) && BracketedPromptStartRegex().IsMatch(line) ||
                // Wrapped continuation (only after already stripping a fragment)
                (!knownPrompt || isUnix) && trailingStrippedCount > 0 && WrappedContinuationPromptRegex().IsMatch(line) ||
                // Bracketed prompt end: ...] $ or ...] #
                (!knownPrompt || isUnix) && BracketedPromptEndRegex().IsMatch(line);

            if (isCompletePrompt)
            {
                endIndex--;
                // trailingStrippedCount++;
                break; // Complete prompt = nothing above can be prompt wrap
            }
            if (isPromptFragment)
            {
                endIndex--;
                trailingStrippedCount++;
            }
            else
            {
                break;
            }
        }

        return string.Join('\n', lines[startIndex..endIndex]);
    }

    /// <summary>
    /// Finds the command echo in the output and returns the content before it (for prompt type detection)
    /// and the lines after the echo (the actual output).
    /// Ported from VS Code's strategyHelpers.ts findCommandEcho.
    ///
    /// The algorithm strips newlines from both output and command, does a substring search,
    /// then maps the match position back to the original line structure.
    /// This handles terminal wrapping that splits the command echo across multiple lines.
    /// </summary>
    private static (string ContentBefore, string[] LinesAfter)? FindCommandEcho(string output, string commandLine, bool allowSuffixMatch)
    {
        var trimmedCommand = commandLine.Trim();
        if (trimmedCommand.Length == 0) return null;

        // Strip newlines from the output so we can find the command as a
        // contiguous substring even when terminal wrapping splits it across lines.
        var (strippedOutput, indexMapping) = StripNewLinesAndBuildMapping(output);
        var matchIndex = strippedOutput.IndexOf(trimmedCommand, StringComparison.Ordinal);

        int matchEndInStripped;
        string contentBefore;

        if (matchIndex != -1)
        {
            // Full command found in the output
            contentBefore = strippedOutput[..matchIndex].Trim();
            matchEndInStripped = matchIndex + trimmedCommand.Length - 1;
        }
        else if (allowSuffixMatch)
        {
            // If the full command wasn't found, check if the output starts with a
            // suffix of the command. This happens when the prompt line is not included,
            // so only the wrapped continuation of the command echo appears at the beginning.
            var suffixLen = 0;
            for (var len = trimmedCommand.Length - 1; len >= 1; len--)
            {
                var suffix = trimmedCommand[^len..];
                if (strippedOutput.StartsWith(suffix, StringComparison.Ordinal))
                {
                    // Require the suffix to start mid-word in the command (not at a word boundary).
                    // A word-boundary match like "MARKER_123" matching the tail of "echo MARKER_123"
                    // is almost certainly actual output, not a wrapped command continuation.
                    var charBefore = trimmedCommand[trimmedCommand.Length - len - 1];
                    if (charBefore is not (' ' or '\t'))
                    {
                        suffixLen = len;
                    }
                    break;
                }
            }
            if (suffixLen == 0) return null;

            contentBefore = "";
            matchEndInStripped = suffixLen - 1;
        }
        else
        {
            return null;
        }

        // Map the match end back to the original output position and determine
        // which line it falls on to split linesAfter.
        var originalEnd = indexMapping[matchEndInStripped];

        var lines = output.Split('\n');
        var echoEndLine = 0;
        var offset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var lineEnd = offset + lines[i].Length; // excludes the \n
            if (offset <= originalEnd && originalEnd <= lineEnd)
            {
                echoEndLine = i + 1;
                break;
            }
            offset = lineEnd + 1; // +1 for the \n
        }

        return (contentBefore, lines[echoEndLine..]);
    }

    /// <summary>
    /// Strips newlines from the output and builds a mapping from stripped indices to original indices.
    /// Ported from VS Code's strategyHelpers.ts stripNewLinesAndBuildMapping.
    /// </summary>
    private static (string StrippedOutput, int[] IndexMapping) StripNewLinesAndBuildMapping(string output)
    {
        var indexMapping = new List<int>(output.Length);
        var strippedChars = new StringBuilder(output.Length);
        for (var i = 0; i < output.Length; i++)
        {
            if (output[i] != '\n')
            {
                strippedChars.Append(output[i]);
                indexMapping.Add(i);
            }
        }
        return (strippedChars.ToString(), indexMapping.ToArray());
    }

    #endregion

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
        return File.Exists(legacyPath) ? legacyPath : null;
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

    [GeneratedRegex(@"\w+@[\w.-]+:")]
    private static partial Regex UnixAtRegex();

    [GeneratedRegex(@"[\w.-]+:\S")]
    private static partial Regex UnixHostRegex();

    [GeneratedRegex(@"^PS\s", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellRegex();

    [GeneratedRegex(@"^[A-Z]:\\")]
    private static partial Regex CmdRegex();

    [GeneratedRegex(@"^\s*\w+@[\w.-]+:.*[#$]\s*$")]
    private static partial Regex BashZshPromptRegex();

    [GeneratedRegex(@"^\s*[\w.-]+:\S.*\s\w+[#$]\s*$")]
    private static partial Regex HostNamePathPromptRegex();

    [GeneratedRegex(@"^PS\s+[A-Z]:\\.*>\s*$")]
    private static partial Regex PowerShellPromptRegex();

    [GeneratedRegex(@"^[A-Z]:\\.*>\s*$")]
    private static partial Regex CmdPromptRegex();

    [GeneratedRegex(@"^\s*[\w/.-]+[#$]\s*$")]
    private static partial Regex WrappedFragmentEndingPromptRegex();

    [GeneratedRegex(@"^\[\s*[\w.-]+(@[\w.-]+)?:[~/]")]
    private static partial Regex BracketedPromptStartRegex();

    [GeneratedRegex(@"^\s*[\w][-\w.]*(@[\w.-]+)?:\S")]
    private static partial Regex WrappedContinuationPromptRegex();

    [GeneratedRegex(@"\]\s*[#$]\s*$")]
    private static partial Regex BracketedPromptEndRegex();

    #endregion

}