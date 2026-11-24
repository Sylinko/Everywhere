using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Everywhere.Chat.Permissions;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Harness;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Watchdog;
using Everywhere.Terminal;
using Tabler.Avalonia;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace Everywhere.Chat.Plugins.BuiltIn;

/// <summary>
/// A unified cross-platform terminal plugin that uses PTY (pseudo-terminal) for shell command execution.
/// Uses Shell Integration (OSC 633 markers) when available (Rich strategy), falls back to
/// idle detection + heuristic cleaning (None strategy) when not available.
/// </summary>
public sealed partial class TerminalPlugin : BuiltInChatPlugin
{
    /// <inheritdoc />
    public override IDynamicLocaleKey HeaderKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Terminal_Header);

    /// <inheritdoc />
    public override IDynamicLocaleKey DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Terminal_Description);

    /// <inheritdoc />
    public override TablerIconKind? Icon => TablerIconKind.Terminal2;

    /// <inheritdoc />
    public override IReadOnlyList<SettingsItem> SettingsItems => _pluginSettings.SettingsItems;

    /// <summary>
    /// The maximum time to wait for a foreground command to complete before it is automatically backgrounded.
    /// </summary>
    public TimeSpan ForegroundBudget { get; init; } = TimeSpan.FromSeconds(20);

    private readonly TerminalPluginSettings _pluginSettings;
    private readonly IWatchdogManager _watchdogManager;
    private readonly ILogger<TerminalPlugin> _logger;

    public TerminalPlugin(Settings settings, IWatchdogManager watchdogManager, ILogger<TerminalPlugin> logger) : base("terminal")
    {
        _pluginSettings = settings.Plugin.Terminal;
        _watchdogManager = watchdogManager;
        _logger = logger;

        _functionsSource.Add(
            new NativeChatFunction(
                ExecuteInTerminalAsync,
                permissions: ChatFunctionPermissions.ShellExecute,
                canBypassApproval: false,
                supportsBackgrounding: true,
                permissionConsentHook: BypassPermissionConsentHook));
    }

    public override ValueTask<IReadOnlyList<ChatFunction>> GetAvailableFunctionsAsync(CancellationToken cancellationToken)
    {
        var configuredFunction = _functionsSource.Items[0];
        var (shellPath, shellType) = DetectShell();
        if (shellType == ShellType.Unknown)
        {
            return new ValueTask<IReadOnlyList<ChatFunction>>([]);
        }

        var shellKind = ShellIntegrationScript.GetShellKind(shellType, shellPath);
        var shellName = shellKind switch
        {
            "pwsh" => "pwsh",
            "powershell" => "Windows PowerShell",
            _ => shellType.ToString()
        };
        var chainHint = shellType switch
        {
            ShellType.PowerShell when shellKind != "pwsh" =>
                "Use semicolons ; to chain commands on one line, NEVER use && even when asked explicitly. " +
                "In PowerShell, use exit $LASTEXITCODE if you need to forward an exact native executable exit code.",
            ShellType.PowerShell =>
                "Prefer ; when chaining commands on one line (pwsh supports &&). " +
                "In PowerShell, use exit $LASTEXITCODE if you need to forward an exact native executable exit code.",
            _ => "Prefer ; when chaining commands on one line."
        };
        var description =
            $"Executes command in {shellName} and obtains its output. " +
            $"The execution is hosted by PTY which is presented to user as an interactive terminal, allowing real-time output display. " +
            $"Long-running commands automatically continue as background jobs. {chainHint}";

        var runtimeFunction = new NativeChatFunction(
            ExecuteInTerminalAsync,
            configuredFunction.Permissions,
            icon: configuredFunction.Icon,
            isDefaultEnabled: configuredFunction.IsDefaultEnabled,
            isDefaultBypassApproval: configuredFunction.IsDefaultBypassApproval,
            canBypassApproval: configuredFunction.CanBypassApproval,
            isExperimental: configuredFunction.IsExperimental,
            isVisible: configuredFunction.IsVisible,
            supportsBackgrounding: configuredFunction.SupportsBackgrounding,
            permissionConsentHook: configuredFunction.PermissionConsentHook,
            functionName: "execute_in_terminal",
            description: description,
            headerKey: configuredFunction.HeaderKey,
            descriptionKey: configuredFunction.DescriptionKey);

        return ValueTask.FromResult<IReadOnlyList<ChatFunction>>([runtimeFunction]);
    }

    [ChatFunction("execute_in_terminal")]
    [DynamicLocaleKey(
        LocaleKey.BuiltInChatPlugin_Terminal_ExecuteScript_Header,
        LocaleKey.BuiltInChatPlugin_Terminal_ExecuteScript_Description)]
    private async Task<ToolContentResult> ExecuteInTerminalAsync(
        [FromChatServices] IChatPluginUserInterface userInterface,
        [FromChatServices] IChatPluginExecutionContext executionContext,
        [FromChatServices] ChatContext chatContext,
        [Description("A concise description for user, explaining what are you doing")] string description,
        [Description("Single or multi-line shell command")] string command,
        CancellationToken cancellationToken
    )
    {
        LogExecutingShellScript(description);

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Script cannot be empty or whitespace.", nameof(command));
        }

        // The command preview remains a single lightweight TextBlock while the detailed terminal
        // block below is created only when the activity item is explicitly expanded.
        await userInterface.SetActivityPreviewAsync(new ChatPluginCommandActivityPreview(command));

        var (shellPath, shellType) = DetectShell();
        if (shellType == ShellType.Unknown)
        {
            throw new HandledException(
                new NotSupportedException(
                    $"The configured shell '{shellPath}' is unavailable or unsupported. " +
                    $"Supported shells are PowerShell (pwsh), Windows PowerShell (powershell), zsh, and bash. " +
                    $"Ask the user to check the Terminal plugin's shell-path setting."),
                LocaleKey.BuiltInChatPlugin_Terminal_UnsupportedShell);
        }

        var consent = await userInterface.RequestConsentAsync(
            null,
            new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Terminal_ExecuteScript_ScriptConsent_Header),
            new ChatPluginContainerDisplayBlock
            {
                new ChatPluginTextDisplayBlock(description),
                new ChatPluginCodeBlockDisplayBlock(command, DetectLanguageHint(shellPath)),
            },
            RequestConsentRememberMasks.AllowOnce | RequestConsentRememberMasks.AllowSession,
            approvalScope: ToolApprovalScope.Create(
                "Shell execution",
                new ToolApprovalShellScope(shellPath, shellType.ToString(), command, description),
                ToolApprovalInputJsonSerializerContext.ForPrompt.ToolApprovalShellScope),
            cancellationToken: cancellationToken);

        if (!consent)
        {
            throw new HandledException(
                new UnauthorizedAccessException(
                    consent.FormatReason("Approval denied the shell-script execution approval request, so the command was not run.")),
                LocaleKey.BuiltInChatPlugin_Terminal_ExecuteScript_DenyMessage);
        }

        var shellKind = ShellIntegrationScript.GetShellKind(shellType, shellPath);
        // ReSharper disable once ExplicitCallerInfoArgument
        using var executeActivity = TerminalTelemetry.ActivitySource.StartActivity("terminal.execute");
        executeActivity?.SetTag("terminal.shell.kind", shellKind);
        executeActivity?.SetTag("terminal.shell.type", shellType.ToString());
        var startTimestamp = Stopwatch.GetTimestamp();

        var terminalDimensions = TerminalDimensions.Default;
        var shellArgs = ShellIntegrationScript.BuildShellArgs(shellType, shellPath);
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
            Cwd = chatContext.WorkingDirectory,
            App = shellPath,
            CommandLine = shellArgs ?? [],
            VerbatimCommandLine = true,
            Environment = environment,
        };

        var terminalRuns = new List<TerminalRun>();
        var strategyName = "none";
        var outcomeName = "unknown";
        var errorType = "none";
        var bracketedPasteEnabled = false;
        IPtyConnection? pty = null;
        TerminalSession? session = null;
        IAsyncDisposable? watchdogRegistration = null;
        try
        {
            pty = await PtyProvider.SpawnAsync(options, cancellationToken);
            session = TerminalSession.FromPtyOptions(pty, options);
            watchdogRegistration = await _watchdogManager.RegisterProcessAsync(pty.Pid);
            using var foregroundBudget = executionContext.BeginForegroundBudget(ForegroundBudget);

            // Detect shell integration and choose strategy
            // If scripts are not available (shellArgs is null), skip detection and use None directly
            ExecuteStrategy strategy;
            if (shellArgs is null)
            {
                LogShellIntegrationScriptNotAvailable(shellKind, shellType);
                TerminalTelemetry.StrategyDetections.Add(
                    1,
                    new TagList
                    {
                        { "shell.kind", shellKind },
                        { "strategy", "none" },
                        { "reason", "script_missing" },
                        { "error.type", "none" }
                    });
                strategy = new NoneExecuteStrategy(_logger);
            }
            else
            {
                strategy = await ExecuteStrategy.DetectStrategyAsync(session, shellType, _logger, cancellationToken, shellPath);
            }

            strategyName = strategy is RichExecuteStrategy ? "rich" : "none";
            var execution = strategy.ExecuteAsync(session, command, shellType, timeout: null, cancellationToken);
            await foreach (var run in execution)
            {
                terminalRuns.Add(run);

                var displayBlock = new ChatPluginTerminalDisplayBlock(shellType, run, session);
                await userInterface.DisplaySink.AppendBlockAsync(displayBlock);

                try
                {
                    await run.WaitAsync(cancellationToken);
                }
                finally
                {
                    await chatContext.DispatchAsync(() => displayBlock.Complete(
                        run.ExitCode,
                        run.Termination,
                        cancellationToken.IsCancellationRequested));
                }
            }

            bracketedPasteEnabled = session.Parser.IsBracketedPasteModeEnabled;
        }
        catch (OperationCanceledException)
        {
            outcomeName = "canceled";
            errorType = nameof(OperationCanceledException);
            executeActivity?.SetTag("terminal.canceled", true);
            if (pty is not null)
            {
                try { pty.Kill(); }
                catch
                {
                    // Ignore kill errors during cancellation teardown.
                }
            }

            throw;
        }
        catch (Exception ex)
        {
            outcomeName = pty is null ? "spawn_failed" : "error";
            errorType = ex.GetType().Name;
            executeActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            executeActivity?.SetTag("error.type", errorType);
            throw;
        }
        finally
        {
            session?.DiscardPendingRead();
            pty?.Dispose();
            if (watchdogRegistration is not null)
            {
                await watchdogRegistration.DisposeAsync();
            }

            var isBackgrounded = executionContext.IsBackgrounded;
            if (outcomeName == "unknown")
            {
                outcomeName = terminalRuns.AsValueEnumerable().Any(static r => r.Termination is { Kind: PtyTerminationKind.Signaled })
                    ? "signaled"
                    : terminalRuns.AsValueEnumerable().Any(static r => r.ExitCode is not null and not 0)
                        ? "nonzero_exit"
                        : "success";
            }

            var duration = Stopwatch.GetElapsedTime(startTimestamp);
            executeActivity?.SetTag("terminal.strategy", strategyName);
            executeActivity?.SetTag("terminal.outcome", outcomeName);
            executeActivity?.SetTag("terminal.backgrounded", isBackgrounded);
            executeActivity?.SetTag("terminal.run_count", terminalRuns.Count);
            executeActivity?.SetTag("terminal.bracketed_paste", bracketedPasteEnabled);

            TerminalTelemetry.Executions.Add(
                1,
                new TagList
                {
                    { "shell.kind", shellKind },
                    { "strategy", strategyName },
                    { "outcome", outcomeName },
                    { "backgrounded", isBackgrounded },
                    { "error.type", errorType }
                });
            TerminalTelemetry.ExecutionDuration.Record(
                duration.TotalSeconds,
                new TagList
                {
                    { "shell.kind", shellKind },
                    { "strategy", strategyName },
                    { "backgrounded", isBackgrounded }
                });
        }

        var bodyBuilder = new StringBuilder();
        var hasEarlierOmission = false;
        var isOutputIncomplete = false;
        foreach (var run in terminalRuns)
        {
            isOutputIncomplete |= run.IsOutputIncomplete;
            var (runText, isRunOmitted) = run.Output.GetPreviewParts();
            if (isRunOmitted)
            {
                hasEarlierOmission = true;
            }

            var trimmedRunText = runText.TrimEnd();
            if (trimmedRunText.Length == 0) continue;

            if (bodyBuilder.Length > 0)
            {
                bodyBuilder.Append('\n');
            }

            bodyBuilder.Append(trimmedRunText);
        }

        var resultBuilder = new StringBuilder();
        var boundedBody = TerminalLineBuffer.TruncateTail(
            bodyBuilder.ToString(),
            TerminalLineBuffer.DefaultPreviewLength,
            hasEarlierOmission);
        if (boundedBody.Length > 0)
        {
            resultBuilder.AppendLine(boundedBody);
        }

        var signaledTermination = terminalRuns.AsValueEnumerable()
            .Where(static r => r.Termination is { Kind: PtyTerminationKind.Signaled })
            .Select(static r => r.Termination)
            .LastOrDefault();
        var finalExitCode = terminalRuns.AsValueEnumerable()
            .Select(static r => r.ExitCode)
            .LastOrDefault(static code => code is not null);
        var statusLine = signaledTermination is { Kind: PtyTerminationKind.Signaled } signaled
            ? $"Terminated by signal: {signaled.Value}"
            : finalExitCode is { } exitCode
                ? $"Exit code: {exitCode}"
                : "Exit code: N/A";
        resultBuilder.AppendLine(statusLine);
        if (isOutputIncomplete)
        {
            resultBuilder.AppendLine("[output may be incomplete: child process still holds PTY open]");
        }

        return new ToolContentResult
        {
            Blocks = [new ToolContentBlock.Text(resultBuilder.TrimEnd().ToString())],
            IsError = terminalRuns.AsValueEnumerable().Any(static r =>
                r.Termination is { Kind: PtyTerminationKind.Signaled } || r.ExitCode is not null and not 0)
        };
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

    [LoggerMessage(LogLevel.Debug, "Executing shell script with description: {Description}")]
    partial void LogExecutingShellScript(string description);

    [LoggerMessage(LogLevel.Warning, "Shell integration script not available for {ShellKind} ({ShellType}); falling back to None strategy.")]
    partial void LogShellIntegrationScriptNotAvailable(string shellKind, ShellType shellType);
}
