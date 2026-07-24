using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Everywhere.AI;
using Everywhere.Chat.Permissions;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Terminal;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Porta.Pty;

namespace Everywhere.Chat.Plugins.BuiltIn;

/// <summary>
/// A unified cross-platform terminal plugin that uses PTY (pseudo-terminal) for shell command execution.
/// Uses Shell Integration (OSC 633 markers) when available (Rich strategy), falls back to
/// idle detection + heuristic cleaning (None strategy) when not available.
/// </summary>
public sealed partial class TerminalPlugin : BuiltInChatPlugin
{
    public override IDynamicLocaleKey HeaderKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Terminal_Header);
    public override IDynamicLocaleKey DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Terminal_Description);
    public override LucideIconKind? Icon => LucideIconKind.SquareTerminal;
    // TODO: uncomment this after Agentic Approval is done
    // public override bool IsDefaultEnabled => true;
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
                permissions: ChatFunctionPermissions.ShellExecute,
                isAutoApproveAllowed: false,
                onPermissionConsent: _ => true));
    }

    public override ValueTask<IReadOnlyList<ChatFunction>> GetAvailableFunctionsAsync(CancellationToken cancellationToken)
    {
        var configuredFunction = _functionsSource.Items[0];
        var (shellPath, shellType) = DetectShell();
        if (shellType == ShellType.Unknown)
        {
            return new ValueTask<IReadOnlyList<ChatFunction>>([]);
        }

        var description =
            $"Executes command in {shellType switch {
                ShellType.PowerShell when shellPath.Contains("pwsh", StringComparison.OrdinalIgnoreCase) => "pwsh",
                ShellType.PowerShell => "Windows PowerShell",
                _ => shellType.ToString()
            }} and obtains its output. The execution is hosted by PTY which is presented to user as an interactive terminal, allowing real-time output display. {shellType switch {
            ShellType.PowerShell => "Use semicolons ; to chain commands on one line, NEVER use && even when asked explicitly",
            _ => "Prefer ; when chaining commands on one line"
        }}";

        var runtimeFunction = new BuiltInChatFunction(
            ExecuteInTerminalAsync,
            configuredFunction.Permissions,
            icon: configuredFunction.Icon,
            isDefaultEnabled: configuredFunction.IsDefaultEnabled,
            isDefaultAutoApprove: configuredFunction.IsDefaultAutoApprove,
            isAutoApproveAllowed: configuredFunction.IsAutoApproveAllowed,
            isExperimental: configuredFunction.IsExperimental,
            isVisible: configuredFunction.IsVisible,
            onPermissionConsent: configuredFunction.OnPermissionConsent,
            functionName: "execute_in_terminal",
            description: description,
            headerKey: configuredFunction.HeaderKey,
            descriptionKey: configuredFunction.DescriptionKey);

        return ValueTask.FromResult<IReadOnlyList<ChatFunction>>([runtimeFunction]);
    }

    [KernelFunction("execute_in_terminal")]
    [DynamicLocaleKey(
        LocaleKey.BuiltInChatPlugin_Terminal_ExecuteScript_Header,
        LocaleKey.BuiltInChatPlugin_Terminal_ExecuteScript_Description)]
    private async Task<string> ExecuteInTerminalAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        [Description("A concise description for user, explaining what are you doing")] string description,
        [Description("Single or multi-line shell command")] string command,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Executing shell script with description: {Description}", description);

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Script cannot be empty or whitespace.", nameof(command));
        }

        // The command preview remains a single lightweight TextBlock while the detailed terminal
        // block below is created only when the activity item is explicitly expanded.
        userInterface.ActivityPreview = new ChatPluginCommandActivityPreview(command);

        var (shellPath, shellType) = DetectShell();
        if (shellType == ShellType.Unknown)
        {
            throw new HandledException(
                new NotSupportedException(
                    $"The configured shell '{shellPath}' is unavailable or unsupported. Supported shells are PowerShell (pwsh), Windows PowerShell (powershell), zsh, and bash. Ask the user to check the Terminal plugin's shell-path setting."),
                LocaleKey.BuiltInChatPlugin_Terminal_UnsupportedShell);
        }

        if (!_pluginSettings.AutoApprove)
        {
            var consent = await userInterface.RequestConsentAsync(
                null,
                new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Terminal_ExecuteScript_ScriptConsent_Header),
                new ChatPluginContainerDisplayBlock
                {
                    new ChatPluginTextDisplayBlock(description),
                    new ChatPluginCodeBlockDisplayBlock(command, DetectLanguageHint(shellPath)),
                },
                RequestConsentRememberMasks.AllowOnce | RequestConsentRememberMasks.AllowSession,
                cancellationToken: cancellationToken);
            if (!consent)
            {
                throw new HandledException(
                    new UnauthorizedAccessException(
                        consent.FormatReason(
                            "The user denied the shell-script execution approval request, so the command was not run.")),
                    LocaleKey.BuiltInChatPlugin_Terminal_ExecuteScript_DenyMessage);
            }
        }

        var terminalDimensions = TerminalDimensions.Default;
        var shellArgs = ShellIntegrationScript.BuildShellArgs(shellType);
        var nonce = Guid.NewGuid().ToString("N")[..16];
        var environment = ShellIntegrationScript.BuildEnvironmentVariables(shellType, nonce);
        if (EnvironmentVariableUtilities.GetLatestPathVariable() is { Length: > 0 } latestPath)
        {
            environment["PATH"] = latestPath;
        }

        var options = new PtyOptions
        {
            Name = "Everywhere-Terminal",
            Cols = terminalDimensions.Columns,
            Rows = terminalDimensions.Rows,
            Cwd = chatContext.EnsureWorkingDirectory(),
            App = shellPath,
            CommandLine = shellArgs ?? [],
            VerbatimCommandLine = true,
            Environment = environment,
        };

        var terminalRuns = new List<TerminalRun>();
        using (var pty = await PtyProvider.SpawnAsync(options, cancellationToken))
        {
            var session = TerminalSession.FromPtyOptions(pty, options);

            var pid = pty.Pid;
            await _watchdogManager.RegisterProcessAsync(pid);

            try
            {
                // Detect shell integration and choose strategy
                // If scripts are not available (shellArgs is null), skip detection and use None directly
                ExecuteStrategy strategy;
                if (shellArgs is null)
                {
                    _logger.LogDebug("Shell integration scripts not available for {ShellType}, using None strategy", shellType);
                    strategy = new NoneExecuteStrategy(_logger);
                }
                else
                {
                    strategy = await ExecuteStrategy.DetectStrategyAsync(session, shellType, _logger, cancellationToken);
                }

                var execution = strategy.ExecuteAsync(session, command, shellType, TimeSpan.FromSeconds(30), cancellationToken);
                await foreach (var run in execution)
                {
                    terminalRuns.Add(run);

                    var displayBlock = new ChatPluginTerminalDisplayBlock(shellType, run, session);
                    userInterface.DisplaySink.AppendBlock(displayBlock);

                    try
                    {
                        await run.WaitAsync(cancellationToken);
                    }
                    finally
                    {
                        displayBlock.Complete(run.ExitCode);
                    }
                }

                _logger.LogInformation("[PTY] After execute, IsBracketedPasteModeEnabled={IsEnabled}", session.Parser.IsBracketedPasteModeEnabled);
            }
            finally
            {
                // Unregister from Watchdog and kill the shell process
                await _watchdogManager.UnregisterProcessAsync(pid, killIfRunning: true);
            }
        }

        var outputs = terminalRuns.AsValueEnumerable().Select(run => run.OutputText.Trim()).ToArray();
        var budgets = TokenBudget.Allocate(
            outputs.AsValueEnumerable().Select(TokenHelper.EstimateTokenCount).ToArray().AsSpan(),
            40000);

        var resultBuilder = new StringBuilder();
        for (var i = 0; i < terminalRuns.Count; i++)
        {
            TokenHelper.OmitTo(outputs[i], resultBuilder, budgets[i]);
            resultBuilder.AppendLine().Append("Exit code: ").AppendLine(terminalRuns[i].ExitCode?.ToString() ?? "N/A").AppendLine();
        }

        return resultBuilder.TrimEnd().ToString();
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
