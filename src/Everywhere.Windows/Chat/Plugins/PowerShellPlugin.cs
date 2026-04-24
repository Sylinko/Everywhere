using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using DynamicData;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.I18N;
using Everywhere.Interop;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Everywhere.Windows.Chat.Plugins;

public sealed class PowerShellPlugin : BuiltInChatPlugin
{
    public override IDynamicResourceKey HeaderKey { get; } = new DynamicResourceKey(LocaleKey.Windows_BuiltInChatPlugin_PowerShell_Header);

    public override IDynamicResourceKey DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.Windows_BuiltInChatPlugin_PowerShell_Description);

    public override LucideIconKind? Icon => LucideIconKind.SquareTerminal;

    public override string BeautifulIcon => "avares://Everywhere/Assets/Icons/PowerShell.svg";

    private readonly ILogger<PowerShellPlugin> _logger;

    public PowerShellPlugin(ILogger<PowerShellPlugin> logger) : base("powershell")
    {
        _logger = logger;

        _functionsSource.Add(
            new BuiltInChatFunction(
                ExecuteScriptAsync,
                ChatFunctionPermissions.ShellExecute,
                isAutoApproveAllowed: false,
                onPermissionConsent: _ => true));  // Always allow. The consent will be handled in the function implementation to support both single-line and multi-line scripts.
    }

    [KernelFunction("execute_script")]
    [Description("Execute PowerShell script and obtain its output.")]
    [DynamicResourceKey(LocaleKey.Windows_BuiltInChatPlugin_PowerShell_ExecuteScript_Header)]
    private async Task<string> ExecuteScriptAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        [Description("A concise description for user, explaining what you are doing")] string description,
        [Description("Single or multi-line")] string script,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Executing PowerShell script with description: {Description}", description);

        if (string.IsNullOrWhiteSpace(script))
        {
            throw new ArgumentException("Script cannot be null or empty.", nameof(script));
        }

        string? consentKey;
        var trimmedScript = script.AsSpan().Trim();
        if (!trimmedScript.Contains('\n'))
        {
            // single line script, confirm with user
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
            new ChatPluginCodeBlockDisplayBlock(script, "powershell"),
        };

        var consent = await userInterface.RequestConsentAsync(
            consentKey,
            new DynamicResourceKey(LocaleKey.Windows_BuiltInChatPlugin_PowerShell_ExecuteScript_ScriptConsent_Header),
            detailBlock,
            canRemember: false,
            cancellationToken: cancellationToken);
        if (!consent)
        {
            throw new HandledException(
                new UnauthorizedAccessException(consent.FormatReason("User denied consent for PowerShell script execution.")),
                new DynamicResourceKey(LocaleKey.Windows_BuiltInChatPlugin_PowerShell_ExecuteScript_DenyMessage),
                showDetails: false);
        }

        userInterface.DisplaySink.AppendBlocks(detailBlock);

        var path = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? ".";
        var psi = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(Path.Combine(path, "Everywhere.Windows.PowerShell.exe")),
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false), // remove BOM header
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = chatContext.EnsureWorkingDirectory(),
        };

        // Ensure the latest PATH variable is used
        if (EnvironmentVariableUtilities.GetLatestPathVariable() is { Length: > 0 } latestPath) psi.EnvironmentVariables["Path"] = latestPath;

        string result;
        using (var process = Process.Start(psi))
        {
            if (process is null)
            {
                throw new SystemException("Failed to start PowerShell script execution process.");
            }

            var pid = process.Id;
            await using var registration = cancellationToken.Register(() =>
            {
                // ReSharper disable once MethodSupportsCancellation
                Task.Run(() =>
                {
                    try
                    {
                        Process.Start(
                            new ProcessStartInfo
                            {
                                FileName = "taskkill",
                                Arguments = $"/PID {pid} /T /F",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                            });
                    }
                    catch
                    {
                        // ignore
                    }
                });
            });

            await process.StandardInput.WriteAsync(script);
            process.StandardInput.Close();

            result = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorOutput = await process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new HandledException(
                    new SystemException($"PowerShell script execution failed: {errorOutput}"),
                    new FormattedDynamicResourceKey(
                        LocaleKey.Windows_BuiltInChatPlugin_PowerShell_ExecuteScript_ErrorMessage,
                        new DirectResourceKey(errorOutput.Trim())),
                    showDetails: false);
            }
        }

        userInterface.DisplaySink.AppendCodeBlock(result.Trim(), "log");
        return result;
    }
}