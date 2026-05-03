using System.ComponentModel;
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
/// Uses Shell Integration (OSC 633 markers) when available (Rich strategy), falls back to
/// idle detection + heuristic cleaning (None strategy) when not available.
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
    [Description("Executes script in PowerShell and obtains its output. Each line is sent as a separate Enter keypress.")]
#elif MACOS
    [Description("Executes script in zsh and obtains its output. Each line is sent as a separate Enter keypress.")]
#else
    [Description("Executes script in bash and obtains its output. Each line is sent as a separate Enter keypress.")]
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
            throw new ArgumentException("Script cannot be empty or whitespace.", nameof(script));
        }

        var (shellPath, shellType) = DetectShell();
        if (shellType == ShellType.Unknown)
        {
            throw new HandledException(
                new NotSupportedException(
                    $"Unsupported shell or not found: {shellPath}. Only PowerShell (pwsh), Windows PowerShell (powershell), zsh, and bash are supported."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_Terminal_UnsupportedShell),
                showDetails: false);
        }

        // Consent
        var isMultiline = OutputCleaner.IsMultilineCommand(script);
        string? consentKey;
        if (isMultiline)
        {
            consentKey = "multi-line";
        }
        else
        {
            var trimmedScript = script.AsSpan().Trim();
            var command = trimmedScript[trimmedScript.Split(' ').FirstOrDefault(new Range(0, trimmedScript.Length))].ToString();
            consentKey = $"single.{command}";
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
            canRemember: consentKey[0] == 's',
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

        // Generate nonce for shell integration marker verification
        var nonce = Guid.NewGuid().ToString("N")[..16];

        // Build shell integration args and environment
        var shellArgs = ShellIntegrationScript.BuildShellArgs(shellType);
        var environment = ShellIntegrationScript.BuildEnvironmentVariables(shellType, nonce);

        if (EnvironmentVariableUtilities.GetLatestPathVariable() is { Length: > 0 } latestPath)
        {
            environment["PATH"] = latestPath;
        }

        var options = new PtyOptions
        {
            Name = "Everywhere-Terminal",
            Cols = 1024,
            Rows = 50,
            Cwd = workingDirectory,
            App = shellPath,
            CommandLine = shellArgs ?? [],
            VerbatimCommandLine = true,
            Environment = environment,
        };

        ExecuteResult executeResult;
        using (var pty = await PtyProvider.SpawnAsync(options, cancellationToken))
        {
            var pid = pty.Pid;

            // Register with Watchdog
            await _watchdogManager.RegisterProcessAsync(pid);

            try
            {
                // Detect shell integration and choose strategy
                // If scripts are not available (shellArgs is null), skip detection and use None directly
                IExecuteStrategy strategy;
                if (shellArgs is null)
                {
                    _logger.LogDebug("Shell integration scripts not available for {ShellType}, using None strategy", shellType);
                    strategy = new NoneExecuteStrategy(_logger);
                }
                else
                {
                    strategy = await IExecuteStrategy.DetectStrategyAsync(pty, shellType, _logger, cancellationToken);
                }

                // Execute the command using the chosen strategy
                executeResult = await strategy.ExecuteAsync(
                    pty,
                    script,
                    isMultiline,
                    TimeSpan.FromSeconds(30),
                    cancellationToken);
            }
            finally
            {
                // Unregister from Watchdog and kill the shell process
                await _watchdogManager.UnregisterProcessAsync(pid, killIfRunning: true);
            }
        }

        var result = executeResult.Output;

        // Display exit code if non-zero
        if (executeResult.ExitCode is > 0)
        {
            _logger.LogInformation("Command exited with code {ExitCode}", executeResult.ExitCode);
        }

        userInterface.DisplaySink.AppendCodeBlock(result.Trim(), "log");
        return result;
    }

    /// <summary>
    /// Detect the platform shell and its type.
    /// </summary>
    private (string ShellPath, ShellType Type) DetectShell()
    {
        if (!_pluginSettings.ShellPath.IsNullOrWhiteSpace())
        {
            var type = DetectShellType(_pluginSettings.ShellPath);
            return (_pluginSettings.ShellPath, type);
        }

        string shellPath;
        ShellType shellType;

        if (OperatingSystem.IsWindows())
        {
            // Prefer pwsh (7+) over Windows PowerShell (5.1)
            shellPath = FindPowerShellExecutable() ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            shellType = ShellType.PowerShell;
        }
        else if (OperatingSystem.IsMacOS())
        {
            shellPath = "/bin/zsh";
            shellType = ShellType.Zsh;
        }
        else // Linux
        {
            shellPath = "/bin/bash";
            shellType = ShellType.Bash;
        }

        _pluginSettings.ShellPath = shellPath;
        return (shellPath, shellType);
    }

    /// <summary>
    /// Detect the shell type from the executable path.
    /// Returns Unknown for unrecognized shells.
    /// </summary>
    private static ShellType DetectShellType(string shellPath)
    {
        var name = Path.GetFileNameWithoutExtension(shellPath).ToLowerInvariant();
        return name switch
        {
            "powershell" or "pwsh" => ShellType.PowerShell,
            "zsh" => ShellType.Zsh,
            "bash" => ShellType.Bash,
            _ => ShellType.Unknown
        };
    }

    /// <summary>
    /// Detect the language hint for code block display.
    /// </summary>
    private static string DetectLanguageHint(string? shellPath)
    {
        return Path.GetFileNameWithoutExtension(shellPath)?.ToLowerInvariant() switch
        {
            "powershell" or "pwsh" => "PowerShell",
            "sh" => "sh",
            "zsh" => "zsh",
            "bash" => "bash",
            _ => "shell"
        };
    }

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

    #endregion

    [GeneratedRegex(@"^(\d+(\.\d+)*)")]
    private static partial Regex VersionRegex();
}