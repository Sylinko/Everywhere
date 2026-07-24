using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Everywhere.Chat.Documents;
using Everywhere.Chat.Permissions;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat.Plugins.BuiltIn;

public sealed class OfficeCLIPlugin : BuiltInChatPlugin
{
    public override IDynamicLocaleKey HeaderKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_OfficeCLI_Header);
    public override IDynamicLocaleKey DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_OfficeCLI_Description);
    public override LucideIconKind? Icon => LucideIconKind.Coffee;
    public override bool IsDefaultEnabled => true;

    private readonly ILogger<OfficeCLIPlugin> _logger;
    private readonly string _executablePath;

    public OfficeCLIPlugin(ILogger<OfficeCLIPlugin> logger) : base("officecli")
    {
        _logger = logger;

#if WINDOWS
        _executablePath = Path.Combine(AppContext.BaseDirectory, "officecli.exe");
#elif OSX
        _executablePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "MonoBundle", "officecli"));
#elif LINUX
        _executablePath = Path.Combine(AppContext.BaseDirectory, "officecli");
#else
#error Unsupported platform
#endif

        _functionsSource.Add(new BuiltInChatFunction(ExecuteAsync, ChatFunctionPermissions.FileAccess, isDefaultBypassApproval: true));
    }

    [KernelFunction("officecli")]
    [Description(
        """
        AI-friendly CLI for .docx, .xlsx, .pptx. Single binary, no dependencies, no Office installation needed.
        This tool allows you to call `officecli` directly without having to install or locate the executable.
        Please use the `read_file` tool to read `skill://builtin.officecli/SKILL.md` to get detailed calling instructions.
        """)]
    [DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_OfficeCLI_Header, LocaleKey.Empty)]
    private async Task<PromptNode> ExecuteAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        string commandLine,
        CancellationToken cancellationToken)
    {
        userInterface.ActivityPreview = new ChatPluginCommandActivityPreview(commandLine);
        userInterface.DisplaySink.AppendBlock(new ChatPluginCodeBlockDisplayBlock(commandLine, "bash"));

        if (commandLine.StartsWith("officecli"))
        {
            commandLine = commandLine[9..].Trim();
        }
        else
        {
            commandLine = commandLine.Trim();
        }

        using var process = Process.Start(
            new ProcessStartInfo
            {
                FileName = _executablePath,
                Arguments = commandLine,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

        if (process is null)
        {
            throw new InvalidOperationException("Failed to start the officecli process.");
        }

        await using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                // ReSharper disable once AccessToDisposedClosure
                if (!process.HasExited)
                {
                    // ReSharper disable once AccessToDisposedClosure
                    process.Kill();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill the officecli process.");
            }
        });

        // Start both drains before waiting for either result. Reading one redirected pipe to completion
        // before the other can deadlock when the child fills the unread pipe's buffer.
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;

        var hasOutput = !output.IsNullOrEmpty();
        var hasError = !error.IsNullOrEmpty();

        if (!hasOutput && !hasError)
        {
            return $"officecli exited with code {process.ExitCode}. No output was produced.";
        }

        if (!hasOutput || !hasError)
        {
            return new PromptTextChunk(hasOutput ? output : error).BreakOnWhitespace().LimitTokens(40000);
        }

        var result = new PromptElement(
            "result",
            new PromptElement("stdout", new PromptTextChunk(output).BreakOnWhitespace()),
            new PromptElement("stderr", new PromptTextChunk(error).BreakOnWhitespace()));

        if (process.ExitCode != 0)
        {
            result.Attribute("exit_code", process.ExitCode);
        }

        return result.LimitTokens(40000);
    }
}