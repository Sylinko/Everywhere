using System.Text;
using Everywhere.Chat.Plugins.BuiltIn.Terminal;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Porta.Pty;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Everywhere.Core.Tests;

/// <summary>
/// Integration tests for PTY execution strategies (Rich and None) and real shell command execution.
/// Uses real PTY processes for NoneExecuteStrategy tests and simulated PTY output for RichExecuteStrategy tests.
/// </summary>
[TestFixture]
public class PtyExecutionTests
{
    private ILogger _logger = null!;
    private Serilog.Core.Logger _serilogLogger = null!;

    [OneTimeSetUp]
    public void SetupLogger()
    {
        _serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss.fff}][{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddSerilog(_serilogLogger);
        });

        _logger = loggerFactory.CreateLogger<PtyExecutionTests>();
    }

    [OneTimeTearDown]
    public void TearDownLogger()
    {
        _serilogLogger.Dispose();
    }

    #region Shell Detection Helpers

    /// <summary>
    /// Detect the default shell for the current platform.
    /// </summary>
    private static (string ShellPath, ShellType Type) DetectPlatformShell()
    {
        if (OperatingSystem.IsWindows())
        {
            // Try pwsh first, fall back to powershell
            var pwsh = FindInPath("pwsh.exe") ?? FindInPath("pwsh");
            if (pwsh is not null) return (pwsh, ShellType.PowerShell);

            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            return File.Exists(powershell)
                ? (powershell, ShellType.PowerShell)
                : ("powershell", ShellType.PowerShell);
        }

        if (OperatingSystem.IsMacOS())
        {
            return ("/bin/zsh", ShellType.Zsh);
        }

        return ("/bin/bash", ShellType.Bash);
    }

    private static string? FindInPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var path in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
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

    /// <summary>
    /// Build minimal PTY options for testing (without shell integration scripts).
    /// </summary>
    private static PtyOptions BuildTestPtyOptions(string shellPath, ShellType shellType, string? cwd = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NO_COLOR"] = "1",
            ["TERM"] = "xterm-256color",
        };

        // On Windows, ensure PATH is set
        if (OperatingSystem.IsWindows())
        {
            var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
            var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
            var processPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);
            var combined = string.Join(Path.PathSeparator,
                new[] { machinePath, userPath, processPath }.Where(s => !string.IsNullOrEmpty(s)));
            if (!string.IsNullOrEmpty(combined))
                environment["PATH"] = combined;
        }

        string[] args;
        switch (shellType)
        {
            case ShellType.PowerShell:
                args = ["-NoProfile", "-NoLogo"];
                break;
            case ShellType.Zsh:
                args = [];
                environment["HISTFILE"] = "/dev/null";
                break;
            case ShellType.Bash:
                args = ["--norc", "--noprofile"];
                environment["HISTFILE"] = "/dev/null";
                break;
            default:
                args = [];
                break;
        }

        return new PtyOptions
        {
            Name = "Everywhere-Test",
            Cols = 1024,
            Rows = 50,
            Cwd = cwd ?? Directory.GetCurrentDirectory(),
            App = shellPath,
            CommandLine = args,
            VerbatimCommandLine = true,
            Environment = environment,
        };
    }

    #endregion

    #region NoneExecuteStrategy — Real PTY Tests

    [Test]
    public async Task NoneStrategy_SingleLineCommand_Echo()
    {
        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Testing single-line command on {Shell} ({ShellType})", shellPath, shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);

        var strategy = new NoneExecuteStrategy(_logger);
        var script = shellType == ShellType.PowerShell
            ? "Write-Host \"HELLO_PTY_TEST\""
            : "echo \"HELLO_PTY_TEST\"";

        var result = await strategy.ExecuteAsync(pty, script, isMultiline: false, TimeSpan.FromSeconds(15), CancellationToken.None);

        _logger.LogInformation("Output: {Output}", result.Output);
        _logger.LogInformation("ExitCode: {ExitCode}", result.ExitCode);

        Assert.That(result.Output, Does.Contain("HELLO_PTY_TEST"),
            $"Expected output to contain 'HELLO_PTY_TEST'. Actual output:\n{result.Output}");
    }

    [Test]
    public async Task NoneStrategy_MultiLineCommand()
    {
        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Testing multi-line command on {Shell} ({ShellType})", shellPath, shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);

        var strategy = new NoneExecuteStrategy(_logger);
        string script;
        if (shellType == ShellType.PowerShell)
        {
            script = "Write-Host \"LINE_A\"\nWrite-Host \"LINE_B\"";
        }
        else
        {
            script = "echo \"LINE_A\"\necho \"LINE_B\"";
        }

        var result = await strategy.ExecuteAsync(pty, script, isMultiline: true, TimeSpan.FromSeconds(15), CancellationToken.None);

        _logger.LogInformation("Output: {Output}", result.Output);

        Assert.That(result.Output, Does.Contain("LINE_A"),
            $"Expected output to contain 'LINE_A'. Actual output:\n{result.Output}");
        Assert.That(result.Output, Does.Contain("LINE_B"),
            $"Expected output to contain 'LINE_B'. Actual output:\n{result.Output}");
    }

    [Test]
    public async Task NoneStrategy_EmptyOutputCommand()
    {
        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Testing empty output command on {Shell} ({ShellType})", shellPath, shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);

        var strategy = new NoneExecuteStrategy(_logger);
        // Use a command that genuinely produces no output
        // $null works in PowerShell but can be corrupted by stray control chars;
        // [void]0 is more robust as it's a pure expression with no side effects.
        var script = shellType == ShellType.PowerShell ? "[void]0" : "true";

        var result = await strategy.ExecuteAsync(pty, script, isMultiline: false, TimeSpan.FromSeconds(15), CancellationToken.None);

        _logger.LogInformation("Output length: {Length}, Output: '{Output}'", result.Output.Length, result.Output);

        // Output should be empty or only whitespace
        Assert.That(result.Output.Trim(), Is.EqualTo(string.Empty).Or.Length.LessThanOrEqualTo(5),
            $"Expected empty or near-empty output. Actual output:\n'{result.Output}'");
    }

    [Test]
    public async Task NoneStrategy_SpecialCharacters()
    {
        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Testing special characters on {Shell} ({ShellType})", shellPath, shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);

        var strategy = new NoneExecuteStrategy(_logger);
        // Use a unique marker to identify our output
        var script = shellType == ShellType.PowerShell
            ? "Write-Host \"SPECIAL_$HOME_TEST\""
            : "echo \"SPECIAL_$HOME_TEST\"";

        var result = await strategy.ExecuteAsync(pty, script, isMultiline: false, TimeSpan.FromSeconds(15), CancellationToken.None);

        _logger.LogInformation("Output: {Output}", result.Output);

        // The $HOME part may or may not be expanded depending on quoting, but our marker should be there
        Assert.That(result.Output, Does.Contain("SPECIAL_"),
            $"Expected output to contain 'SPECIAL_'. Actual output:\n{result.Output}");
    }

    [Test]
    public async Task NoneStrategy_Timeout_GracefulHandling()
    {
        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Testing timeout on {Shell} ({ShellType})", shellPath, shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);

        var strategy = new NoneExecuteStrategy(_logger);
        var script = shellType == ShellType.PowerShell ? "Start-Sleep -Seconds 60" : "sleep 60";

        // Use a very short timeout
        var result = await strategy.ExecuteAsync(pty, script, isMultiline: false, TimeSpan.FromSeconds(3), CancellationToken.None);

        _logger.LogInformation("Output after timeout: '{Output}'", result.Output);

        // Should not throw — just return whatever was captured
        Assert.Pass("Timeout handled gracefully");
    }

    [Test]
    public async Task NoneStrategy_MultilineHeredoc_Bash()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Heredoc test is only applicable on Unix shells");
            return;
        }

        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Testing heredoc on {Shell} ({ShellType})", shellPath, shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);

        var strategy = new NoneExecuteStrategy(_logger);
        var script = "cat <<'EOF'\nHEREDOC_LINE_1\nHEREDOC_LINE_2\nEOF";

        var result = await strategy.ExecuteAsync(pty, script, isMultiline: true, TimeSpan.FromSeconds(15), CancellationToken.None);

        _logger.LogInformation("Output: {Output}", result.Output);

        Assert.That(result.Output, Does.Contain("HEREDOC_LINE_1"),
            $"Expected output to contain 'HEREDOC_LINE_1'. Actual output:\n{result.Output}");
        Assert.That(result.Output, Does.Contain("HEREDOC_LINE_2"),
            $"Expected output to contain 'HEREDOC_LINE_2'. Actual output:\n{result.Output}");
    }

    #endregion

    #region RichExecuteStrategy — Simulated PTY Output Tests

    /// <summary>
    /// Create a mock IPtyConnection with a pre-built ReaderStream containing the given content.
    /// The WriterStream is a MemoryStream that captures written data.
    /// </summary>
    private static (IPtyConnection pty, MemoryStream writerStream) CreateMockPty(byte[] readerContent)
    {
        var readerStream = new MemoryStream(readerContent);
        var writerStream = new MemoryStream();
        var pty = Substitute.For<IPtyConnection>();
        pty.ReaderStream.Returns(readerStream);
        pty.WriterStream.Returns(writerStream);
        pty.Pid.Returns(12345);
        return (pty, writerStream);
    }

    /// <summary>
    /// Build a simulated PTY output sequence with Shell Integration markers (OSC 633).
    /// Sequence: A(PromptStart) → B(CommandReady) → [output] → C(CommandExecuted) → D(CommandFinished;exitCode) → A(PromptStart)
    /// </summary>
    private static byte[] BuildRichSequence(string outputText, int exitCode = 0)
    {
        var sb = new StringBuilder();

        // A — PromptStart
        sb.Append("\e]633;A\x07");
        // B — CommandReady (prompt is ready, at end of prompt line)
        sb.Append("\e]633;B\x07");
        // \r\n — simulate pressing Enter (cursor moves to next line, output starts here)
        sb.Append("\r\n");
        // Command output
        sb.Append(outputText);
        sb.Append("\r\n");
        // C — CommandExecuted
        sb.Append("\e]633;C\x07");
        // D — CommandFinished with exit code
        sb.Append($"\e]633;D;{exitCode}\x07");
        // A — PromptStart (next prompt)
        sb.Append("\e]633;A\x07");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    [Test]
    public async Task RichStrategy_SingleLineCommand_ExtractsOutput()
    {
        var expectedOutput = "HELLO_RICH_TEST";
        var ptyData = BuildRichSequence(expectedOutput);
        var (pty, _) = CreateMockPty(ptyData);

        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(pty, "echo test", isMultiline: false, TimeSpan.FromSeconds(10), CancellationToken.None);

        _logger.LogInformation("Rich output: '{Output}', ExitCode: {ExitCode}", result.Output, result.ExitCode);

        Assert.That(result.Output, Does.Contain(expectedOutput),
            $"Expected output to contain '{expectedOutput}'. Actual output:\n{result.Output}");
        Assert.That(result.ExitCode, Is.EqualTo(0));
    }

    [Test]
    public async Task RichStrategy_MultiLineCommand_ExtractsOutput()
    {
        var expectedOutput = "LINE_A\r\nLINE_B";
        var ptyData = BuildRichSequence(expectedOutput);
        var (pty, _) = CreateMockPty(ptyData);

        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(pty, "echo LINE_A\necho LINE_B", isMultiline: true, TimeSpan.FromSeconds(10), CancellationToken.None);

        _logger.LogInformation("Rich output: '{Output}', ExitCode: {ExitCode}", result.Output, result.ExitCode);

        Assert.That(result.Output, Does.Contain("LINE_A"),
            $"Expected output to contain 'LINE_A'. Actual output:\n{result.Output}");
        Assert.That(result.Output, Does.Contain("LINE_B"),
            $"Expected output to contain 'LINE_B'. Actual output:\n{result.Output}");
    }

    [Test]
    public async Task RichStrategy_NonZeroExitCode()
    {
        var ptyData = BuildRichSequence("error output", exitCode: 42);
        var (pty, _) = CreateMockPty(ptyData);

        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(pty, "exit 42", isMultiline: false, TimeSpan.FromSeconds(10), CancellationToken.None);

        _logger.LogInformation("Rich output: '{Output}', ExitCode: {ExitCode}", result.Output, result.ExitCode);

        Assert.That(result.ExitCode, Is.EqualTo(42));
    }

    [Test]
    public async Task RichStrategy_EmptyOutput()
    {
        var ptyData = BuildRichSequence("", exitCode: 0);
        var (pty, _) = CreateMockPty(ptyData);

        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(pty, "true", isMultiline: false, TimeSpan.FromSeconds(10), CancellationToken.None);

        _logger.LogInformation("Rich output: '{Output}', ExitCode: {ExitCode}", result.Output, result.ExitCode);

        Assert.That(result.ExitCode, Is.EqualTo(0));
        // Output should be empty or near-empty
        Assert.That(result.Output.Trim(), Is.EqualTo(string.Empty).Or.Length.LessThanOrEqualTo(5));
    }

    [Test]
    public async Task RichStrategy_SendsCommandCorrectly_SingleLine()
    {
        var ptyData = BuildRichSequence("ok");
        var (pty, writerStream) = CreateMockPty(ptyData);

        var strategy = new RichExecuteStrategy(_logger);
        await strategy.ExecuteAsync(pty, "echo hello", isMultiline: false, TimeSpan.FromSeconds(10), CancellationToken.None);

        var written = Encoding.UTF8.GetString(writerStream.ToArray());
        _logger.LogInformation("Written to PTY: {Escaped}", OutputCleaner.EscapeForLog(written));

        // Single line: should contain "echo hello\r"
        Assert.That(written, Does.Contain("echo hello"));
        Assert.That(written, Does.Contain("\r"));
        // Should NOT contain bracketed paste markers
        Assert.That(written, Does.Not.Contain("\e[200~"));
    }

    [Test]
    public async Task RichStrategy_SendsCommandCorrectly_MultiLine()
    {
        var ptyData = BuildRichSequence("ok");
        var (pty, writerStream) = CreateMockPty(ptyData);

        var strategy = new RichExecuteStrategy(_logger);
        await strategy.ExecuteAsync(pty, "echo a\necho b", isMultiline: true, TimeSpan.FromSeconds(10), CancellationToken.None);

        var written = Encoding.UTF8.GetString(writerStream.ToArray());
        _logger.LogInformation("Written to PTY: {Escaped}", OutputCleaner.EscapeForLog(written));

        // Multi-line: should use bracketed paste
        Assert.That(written, Does.Contain("\e[200~"), "Should start bracketed paste");
        Assert.That(written, Does.Contain("\e[201~"), "Should end bracketed paste");
        Assert.That(written, Does.Contain("echo a"));
        Assert.That(written, Does.Contain("echo b"));
    }

    [Test]
    public async Task RichStrategy_BracketedPaste_SuppressesExecution()
    {
        // Verify that \r inside bracketed paste does NOT trigger execution.
        // In real shell, bracketed paste wraps the entire multi-line script so the shell
        // treats it as a single paste block, not individual commands.
        var script = "Write-Host 'LINE_A'\rWrite-Host 'LINE_B'";
        var ptyData = BuildRichSequence("LINE_A\r\nLINE_B");
        var (pty, writerStream) = CreateMockPty(ptyData);

        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(pty, script, isMultiline: true, TimeSpan.FromSeconds(10), CancellationToken.None);

        var written = Encoding.UTF8.GetString(writerStream.ToArray());
        _logger.LogInformation("Written to PTY: {Escaped}", OutputCleaner.EscapeForLog(written));

        // Verify bracketed paste markers are present
        Assert.That(written, Does.Contain("\e[200~"), "Should start bracketed paste");
        Assert.That(written, Does.Contain("\e[201~"), "Should end bracketed paste");
        // Verify output contains both lines
        Assert.That(result.Output, Does.Contain("LINE_A"),
            $"Expected output to contain 'LINE_A'. Actual output:\n{result.Output}");
        Assert.That(result.Output, Does.Contain("LINE_B"),
            $"Expected output to contain 'LINE_B'. Actual output:\n{result.Output}");
    }

    [Test]
    public async Task NoneStrategy_MultiLine_SendsLineByLine()
    {
        // Verify that None strategy sends multi-line commands line by line
        // (not as bracketed paste), and correctly collects output from each line.
        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Testing line-by-line sending on {Shell} ({ShellType})", shellPath, shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);

        var strategy = new NoneExecuteStrategy(_logger);
        string script;
        if (shellType == ShellType.PowerShell)
        {
            script = "Write-Host 'SPLIT_A'\nWrite-Host 'SPLIT_B'";
        }
        else
        {
            script = "echo 'SPLIT_A'\necho 'SPLIT_B'";
        }

        var result = await strategy.ExecuteAsync(pty, script, isMultiline: true, TimeSpan.FromSeconds(30), CancellationToken.None);

        _logger.LogInformation("Output: {Output}", result.Output);

        Assert.That(result.Output, Does.Contain("SPLIT_A"),
            $"Expected output to contain 'SPLIT_A'. Actual output:\n{result.Output}");
        Assert.That(result.Output, Does.Contain("SPLIT_B"),
            $"Expected output to contain 'SPLIT_B'. Actual output:\n{result.Output}");
    }

    #endregion

    #region IExecuteStrategy.DetectStrategyAsync

    [Test]
    public async Task DetectStrategy_WithShellIntegration_ReturnsRich()
    {
        // Build output that contains shell integration markers
        var output = BuildRichSequence("prompt ready");
        var readerStream = new MemoryStream(output);
        var writerStream = new MemoryStream();
        var pty = Substitute.For<IPtyConnection>();
        pty.ReaderStream.Returns(readerStream);
        pty.WriterStream.Returns(writerStream);

        var strategy = await IExecuteStrategy.DetectStrategyAsync(pty, ShellType.PowerShell, _logger, CancellationToken.None);

        Assert.That(strategy, Is.TypeOf<RichExecuteStrategy>(),
            "Should detect Shell Integration and return RichExecuteStrategy");
    }

    [Test]
    public async Task DetectStrategy_WithoutShellIntegration_ReturnsNone()
    {
        // Build output without any shell integration markers — just a plain prompt
        var output = Encoding.UTF8.GetBytes("user@host:~$ ");
        var readerStream = new MemoryStream(output);
        var writerStream = new MemoryStream();
        var pty = Substitute.For<IPtyConnection>();
        pty.ReaderStream.Returns(readerStream);
        pty.WriterStream.Returns(writerStream);

        var strategy = await IExecuteStrategy.DetectStrategyAsync(pty, ShellType.Bash, _logger, CancellationToken.None);

        Assert.That(strategy, Is.TypeOf<NoneExecuteStrategy>(),
            "Should not detect Shell Integration and return NoneExecuteStrategy");
    }

    #endregion

    #region Cross-Platform Shell Execution Matrix

    [Test]
    public async Task Matrix_PowerShell_SingleLine()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("PowerShell test only runs on Windows");
            return;
        }

        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation($"Detected shell: {shellPath} (type: {shellType})");
        Assert.That(shellType, Is.EqualTo(ShellType.PowerShell));

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);

        var strategy = new NoneExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(pty, "Write-Host \"MATRIX_PS_TEST\"", isMultiline: false, TimeSpan.FromSeconds(15), CancellationToken.None);

        _logger.LogInformation("[PowerShell] Output: {Output}", result.Output);
        Assert.That(result.Output, Does.Contain("MATRIX_PS_TEST"));
    }

    [Test]
    public async Task Matrix_PowerShell_MultiLine()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("PowerShell test only runs on Windows");
            return;
        }

        var (shellPath, shellType) = DetectPlatformShell();
        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);

        var strategy = new NoneExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(pty, "Write-Host \"PS_LINE1\"\nWrite-Host \"PS_LINE2\"", isMultiline: true, TimeSpan.FromSeconds(15), CancellationToken.None);

        _logger.LogInformation("[PowerShell MultiLine] Output: {Output}", result.Output);
        Assert.That(result.Output, Does.Contain("PS_LINE1"));
        Assert.That(result.Output, Does.Contain("PS_LINE2"));
    }

    [Test]
    public async Task Matrix_Bash_SingleLine()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Bash test only runs on Unix");
            return;
        }

        var (shellPath, shellType) = DetectPlatformShell();
        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);

        var strategy = new NoneExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(pty, "echo \"MATRIX_BASH_TEST\"", isMultiline: false, TimeSpan.FromSeconds(15), CancellationToken.None);

        _logger.LogInformation("[Bash] Output: {Output}", result.Output);
        Assert.That(result.Output, Does.Contain("MATRIX_BASH_TEST"));
    }

    [Test]
    public async Task Matrix_Bash_MultiLine()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Bash test only runs on Unix");
            return;
        }

        var (shellPath, shellType) = DetectPlatformShell();
        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);

        var strategy = new NoneExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(pty, "echo \"BASH_L1\"\necho \"BASH_L2\"", isMultiline: true, TimeSpan.FromSeconds(15), CancellationToken.None);

        _logger.LogInformation("[Bash MultiLine] Output: {Output}", result.Output);
        Assert.That(result.Output, Does.Contain("BASH_L1"));
        Assert.That(result.Output, Does.Contain("BASH_L2"));
    }

    [Test]
    public async Task Matrix_Zsh_SingleLine()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Ignore("Zsh test only runs on macOS");
            return;
        }

        var (shellPath, shellType) = DetectPlatformShell();
        Assert.That(shellType, Is.EqualTo(ShellType.Zsh));

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);

        var strategy = new NoneExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(pty, "echo \"MATRIX_ZSH_TEST\"", isMultiline: false, TimeSpan.FromSeconds(15), CancellationToken.None);

        _logger.LogInformation("[Zsh] Output: {Output}", result.Output);
        Assert.That(result.Output, Does.Contain("MATRIX_ZSH_TEST"));
    }

    [Test]
    public async Task Matrix_Zsh_MultiLine()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Ignore("Zsh test only runs on macOS");
            return;
        }

        var (shellPath, shellType) = DetectPlatformShell();
        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);

        var strategy = new NoneExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(pty, "echo \"ZSH_L1\"\necho \"ZSH_L2\"", isMultiline: true, TimeSpan.FromSeconds(15), CancellationToken.None);

        _logger.LogInformation("[Zsh MultiLine] Output: {Output}", result.Output);
        Assert.That(result.Output, Does.Contain("ZSH_L1"));
        Assert.That(result.Output, Does.Contain("ZSH_L2"));
    }

    #endregion
}
