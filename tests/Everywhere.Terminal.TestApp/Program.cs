using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Everywhere.AI;
using Everywhere.Interop;
using Everywhere.Terminal;
using Microsoft.Extensions.Logging;
using Porta.Pty;

var options = AppOptions.Parse(args);
var logger = new ConsoleLogger();

if (options.Observe)
{
    await ObserveAsync(options, logger);
    return;
}

await ExecuteInteractiveAsync(options, logger);

static async Task ExecuteInteractiveAsync(AppOptions options, ILogger logger)
{
    while (true)
    {
        Console.Write("> ");
        if (Console.ReadLine() is not { } line) return;

        var command = line.StartsWith('"') ? JsonSerializer.Deserialize<string>(line) : line;
        if (command is null) return;

        var terminalOptions = BuildPtyOptions(options);
        var terminalRuns = new List<TerminalRun>();
        using (var pty = await PtyProvider.SpawnAsync(terminalOptions.PtyOptions, CancellationToken.None))
        {
            var session = TerminalSession.FromPtyOptions(pty, terminalOptions.PtyOptions);
            var strategy = await DetectStrategyAsync(session, terminalOptions, logger, CancellationToken.None);

            var execution = strategy.ExecuteAsync(
                session,
                command,
                terminalOptions.ShellType,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            await foreach (var run in execution)
            {
                terminalRuns.Add(run);
                await run.WaitAsync(CancellationToken.None);
            }
        }

        var exitCode = terminalRuns.LastOrDefault()?.ExitCode;
        if (exitCode is > 0)
        {
            logger.LogInformation("Command exited with code {ExitCode}", exitCode);
        }

        var output = string.Join(
            "\n",
            terminalRuns
                .Select(run => run.OutputText)
                .Where(text => !string.IsNullOrEmpty(text)));
        var result = TokenHelper.Omit(output, 8000, "[... OUTPUT OMITTED ...]").Trim();
        Console.WriteLine(result);
    }
}

static async Task ObserveAsync(AppOptions options, ILogger logger)
{
    var terminalOptions = BuildPtyOptions(options);
    var trace = new TerminalTrace();

    trace.Log(
        "SPAWN",
        $"shell={terminalOptions.ShellType} app={terminalOptions.PtyOptions.App} args={string.Join(' ', terminalOptions.PtyOptions.CommandLine ?? [])}");

    using var pty = await PtyProvider.SpawnAsync(terminalOptions.PtyOptions, CancellationToken.None);
    var session = TerminalSession.FromPtyOptions(pty, terminalOptions.PtyOptions);

    void OnMarker(in ShellIntegrationMarker marker)
    {
        var detail = marker.Type switch
        {
            ShellIntegrationMarkerType.CommandLine => $"{marker.Type} line={marker.Line} command={marker.CommandLine}",
            ShellIntegrationMarkerType.CommandFinished => $"{marker.Type} line={marker.Line} exit={marker.ExitCode?.ToString() ?? "<null>"}",
            _ => $"{marker.Type} line={marker.Line}",
        };

        trace.Log(
            "MARKER",
            $"{detail} bracketedPaste={session.Parser.IsBracketedPasteModeEnabled}");
    }

    void OnTerminalResponse(string response)
    {
        trace.Log("REPLY", OutputCleaner.EscapeForLog(response));
    }

    session.Parser.ShellIntegrationMarkerReceived += OnMarker;
    session.Parser.TerminalResponseRequested += OnTerminalResponse;

    var deadline = Stopwatch.StartNew();
    try
    {
        while (deadline.Elapsed < options.Duration)
        {
            var remaining = options.Duration - deadline.Elapsed;
            var readTask = pty.ReaderStream.ReadAsync(session.ReadBuffer).AsTask();
            var completed = await Task.WhenAny(readTask, Task.Delay(remaining));
            if (completed != readTask)
            {
                trace.Log("DONE", $"duration={options.Duration.TotalMilliseconds:0}ms");
                break;
            }

            var bytesRead = await readTask;
            if (bytesRead == 0)
            {
                var flushed = session.TextDecoder.Flush();
                trace.Feed(flushed);
                session.Parser.Feed(flushed);
                break;
            }

            var text = session.TextDecoder.Decode(session.ReadBuffer.AsSpan(0, bytesRead));
            trace.Log("READ", $"bytes={bytesRead} chars={text.Length}");
            trace.Feed(text);
            session.Parser.Feed(text);
            await session.FlushTerminalResponsesAsync(CancellationToken.None);
        }
    }
    finally
    {
        session.Parser.ShellIntegrationMarkerReceived -= OnMarker;
        session.Parser.TerminalResponseRequested -= OnTerminalResponse;

        try
        {
            pty.Kill();
        }
        catch
        {
            // The shell may already be gone.
        }
    }

    trace.Log(
        "STATE",
        $"shellIntegration={session.Parser.HasDetectedShellIntegration} bracketedPaste={session.Parser.IsBracketedPasteModeEnabled} focus={session.Parser.IsFocusEventTrackingEnabled} win32Input={session.Parser.IsWin32InputModeEnabled}");
}

static async Task<ExecuteStrategy> DetectStrategyAsync(
    TerminalSession session,
    TerminalOptions terminalOptions,
    ILogger logger,
    CancellationToken cancellationToken)
{
    if (terminalOptions.ShellArgs is null)
    {
        logger.LogDebug("Shell integration scripts not available for {ShellType}, using None strategy", terminalOptions.ShellType);
        return new NoneExecuteStrategy(logger);
    }

    return await ExecuteStrategy.DetectStrategyAsync(
        session,
        terminalOptions.ShellType,
        logger,
        cancellationToken);
}

static TerminalOptions BuildPtyOptions(AppOptions options)
{
    var (shellPath, shellType) = ResolveShell(options.Shell);
    var terminalDimensions = TerminalDimensions.Default;
    var shellArgs = ShellIntegrationScript.BuildShellArgs(shellType);
    var nonce = Guid.NewGuid().ToString("N")[..16];
    var environment = ShellIntegrationScript.BuildEnvironmentVariables(shellType, nonce);

    if (EnvironmentVariableUtilities.GetLatestPathVariable() is { Length: > 0 } latestPath)
    {
        environment["PATH"] = latestPath;
    }

    var ptyOptions = new PtyOptions
    {
        Name = "Everywhere-Terminal",
        Cols = terminalDimensions.Columns,
        Rows = terminalDimensions.Rows,
        Cwd = Environment.CurrentDirectory,
        App = shellPath,
        CommandLine = shellArgs ?? [],
        VerbatimCommandLine = true,
        Environment = environment,
    };

    return new TerminalOptions(ptyOptions, shellType, shellArgs);
}

static (string ShellPath, ShellType ShellType) ResolveShell(string shell)
{
    return shell.ToLowerInvariant() switch
    {
        "auto" when OperatingSystem.IsWindows() => (FindInPath("pwsh.exe") ?? "powershell.exe", ShellType.PowerShell),
        "auto" when OperatingSystem.IsMacOS() => ("/bin/zsh", ShellType.Zsh),
        "auto" => ("/bin/bash", ShellType.Bash),
        "zsh" => ("/bin/zsh", ShellType.Zsh),
        "bash" => ("/bin/bash", ShellType.Bash),
        "pwsh" => (FindInPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh") ?? "pwsh", ShellType.PowerShell),
        "powershell" => (OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh", ShellType.PowerShell),
        var path when File.Exists(path) => (path, DetectShellType(path)),
        _ => throw new ArgumentException($"Unknown shell '{shell}'. Use auto, zsh, bash, pwsh, powershell, or a shell path.")
    };
}

static ShellType DetectShellType(string shellPath)
{
    return Path.GetFileNameWithoutExtension(shellPath).ToLowerInvariant() switch
    {
        "powershell" or "pwsh" => ShellType.PowerShell,
        "zsh" => ShellType.Zsh,
        "bash" => ShellType.Bash,
        _ => ShellType.Unknown
    };
}

static string? FindInPath(string fileName)
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
            // Ignore invalid PATH entries.
        }
    }

    return null;
}

file sealed record TerminalOptions(PtyOptions PtyOptions, ShellType ShellType, string[]? ShellArgs);

file sealed record AppOptions(string Shell, bool Observe, TimeSpan Duration)
{
    public static AppOptions Parse(string[] args)
    {
        var shell = "auto";
        var observe = false;
        var duration = TimeSpan.FromMilliseconds(2500);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--observe":
                    observe = true;
                    break;
                case "--shell" when i + 1 < args.Length:
                    shell = args[++i];
                    break;
                case "--duration" when i + 1 < args.Length && int.TryParse(args[++i], out var durationMs):
                    duration = TimeSpan.FromMilliseconds(durationMs);
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. Use --help for usage.");
            }
        }

        return new AppOptions(shell, observe, duration);
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            Everywhere.Terminal.TestApp

            Interactive execution:
              dotnet run --project tests/Everywhere.Terminal.TestApp -- --shell auto

            PTY startup observer:
              dotnet run --project tests/Everywhere.Terminal.TestApp -- --observe --shell zsh --duration 2500

            Options:
              --shell auto|zsh|bash|pwsh|powershell|<path>
              --observe
              --duration <milliseconds>
            """);
    }
}

file sealed class TerminalTrace
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _sequence = new();
    private int _eventId;
    private TraceState _state = TraceState.Ground;
    private bool _oscSawEscape;

    public void Feed(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            FeedChar(c);
        }
    }

    public void Log(string kind, string detail)
    {
        Console.WriteLine($"{_eventId++,4:0000} +{_stopwatch.Elapsed.TotalMilliseconds,8:0.0}ms {kind,-8} {detail}");
    }

    private void FeedChar(char c)
    {
        switch (_state)
        {
            case TraceState.Ground:
                if (c == '\e')
                {
                    FlushText();
                    _sequence.Clear().Append(c);
                    _state = TraceState.Escape;
                    return;
                }

                _text.Append(c);
                return;

            case TraceState.Escape:
                _sequence.Append(c);
                switch (c)
                {
                    case '[':
                        _state = TraceState.Csi;
                        return;
                    case ']':
                        _sequence.Clear();
                        _oscSawEscape = false;
                        _state = TraceState.Osc;
                        return;
                    default:
                        Log("ESC", OutputCleaner.EscapeForLog(_sequence.ToString()));
                        _state = TraceState.Ground;
                        return;
                }

            case TraceState.Csi:
                _sequence.Append(c);
                if (c >= 0x40 && c <= 0x7E)
                {
                    var value = _sequence.ToString();
                    Log("CSI", OutputCleaner.EscapeForLog(value));
                    InterpretCsi(value);
                    _sequence.Clear();
                    _state = TraceState.Ground;
                }
                return;

            case TraceState.Osc:
                if (_oscSawEscape)
                {
                    if (c == '\\')
                    {
                        ProcessOsc(_sequence.ToString());
                        _sequence.Clear();
                        _state = TraceState.Ground;
                    }
                    else
                    {
                        _sequence.Append('\e').Append(c);
                        _oscSawEscape = false;
                    }
                    return;
                }

                switch (c)
                {
                    case '\a':
                        ProcessOsc(_sequence.ToString());
                        _sequence.Clear();
                        _state = TraceState.Ground;
                        return;
                    case '\e':
                        _oscSawEscape = true;
                        return;
                    default:
                        _sequence.Append(c);
                        return;
                }
        }
    }

    private void FlushText()
    {
        if (_text.Length == 0) return;

        Log("TEXT", OutputCleaner.EscapeForLog(_text.ToString()));
        _text.Clear();
    }

    private void ProcessOsc(string content)
    {
        if (content.StartsWith("633;", StringComparison.Ordinal))
        {
            var parts = content.Split(';', 3);
            var marker = parts.Length > 1 ? parts[1] : "<missing>";
            var data = parts.Length > 2 ? parts[2] : "";
            Log("OSC633", $"{DecodeMarker(marker)} data={OutputCleaner.EscapeForLog(data)}");
            return;
        }

        Log("OSC", OutputCleaner.EscapeForLog(content));
    }

    private void InterpretCsi(string csi)
    {
        if (csi.Length < 3 || csi[0] != '\e' || csi[1] != '[') return;

        var finalByte = csi[^1];
        var body = csi[2..^1];
        if (body.Length > 0 && body[0] == '?' && (finalByte == 'h' || finalByte == 'l'))
        {
            var enabled = finalByte == 'h';
            foreach (var mode in body[1..].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = mode switch
                {
                    "1004" => "focus-events",
                    "2004" => "bracketed-paste",
                    "9001" => "win32-input",
                    _ => null
                };

                if (name is not null)
                {
                    Log("MODE", $"{name}={(enabled ? "on" : "off")} raw={OutputCleaner.EscapeForLog(csi)}");
                }
            }
        }

        switch (finalByte)
        {
            case 'A' or 'B' or 'C' or 'D' or 'E' or 'F' or 'G' or 'H' or 'f':
                Log("CURSOR", OutputCleaner.EscapeForLog(csi));
                break;
            case 'J':
                Log("ERASE", $"display raw={OutputCleaner.EscapeForLog(csi)}");
                break;
            case 'K':
                Log("ERASE", $"line raw={OutputCleaner.EscapeForLog(csi)}");
                break;
        }
    }

    private static string DecodeMarker(string marker)
    {
        return marker switch
        {
            "A" => "PromptStart(A)",
            "B" => "CommandReady(B)",
            "C" => "CommandExecuted(C)",
            "D" => "CommandFinished(D)",
            "E" => "CommandLine(E)",
            _ => marker
        };
    }

    private enum TraceState
    {
        Ground,
        Escape,
        Csi,
        Osc
    }
}

file class ConsoleLogger : ILogger
{
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Console.ForegroundColor = logLevel switch
        {
            LogLevel.Trace => ConsoleColor.Gray,
            LogLevel.Debug => ConsoleColor.Cyan,
            LogLevel.Information => ConsoleColor.Green,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Critical => ConsoleColor.Magenta,
            _ => ConsoleColor.White,
        };
        Console.WriteLine(formatter(state, exception));
        Console.ResetColor();
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}
