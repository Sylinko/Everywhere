using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Everywhere.Linux.Desktop;
using Everywhere.Linux.ProcessIsolation;
using Serilog;

namespace Everywhere.Linux.Automation;

/// <summary>
/// Supplements toolkit events with Wayland PRIMARY selection changes. This never
/// reads or writes the regular clipboard and never synthesizes Copy. The optional
/// wl-clipboard helper owns the protocol connection; short-lived callbacks relay
/// bounded text over an authenticated, installation-owned pipe.
/// </summary>
public sealed class PrimarySelectionMonitor : IAsyncDisposable
{
    /// <summary>
    /// Limits the selected text relayed by one PRIMARY callback.
    /// </summary>
    public const int MaximumCharacters = 32768;
    private readonly string _pipeName = "everywhere-primary-" + Guid.NewGuid().ToString("N");
    private readonly Action<PrimarySelectionChange> _changed;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;

    /// <summary>
    /// Starts a PRIMARY listener for the explicitly selected Wayland session.
    /// </summary>
    public PrimarySelectionMonitor(Action<PrimarySelectionChange> changed)
    {
        _changed = changed;
        _worker = RunAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    /// <summary>
    /// Relays one wl-paste callback without booting Main or another Host. PRIMARY
    /// has no trustworthy accessible-object identity, so consumers receive text
    /// only and must recheck the captured foreground window before accepting it.
    /// </summary>
    public static async Task RelayAsync(string pipeName, ICompositorStateProvider compositor)
    {
        if (!pipeName.StartsWith("everywhere-primary-", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid primary-selection relay.", nameof(pipeName));
        }

        var clipboardState = Environment.GetEnvironmentVariable("CLIPBOARD_STATE");
        if (clipboardState == "sensitive")
        {
            return;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var window = await compositor.GetActiveWindowAsync(deadline.Token).ConfigureAwait(false);
        if (window is null || window.ProcessId <= 0)
        {
            return;
        }

        var pointer = await compositor.GetGlobalCursorPositionAsync(deadline.Token).ConfigureAwait(false);
        var (text, isIncomplete) = clipboardState is "nil" or "clear"
            ? (string.Empty, false)
            : await Task.Run((
                ) => ReadTextAsync(Console.In, deadline.Token), deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        var change = new PrimarySelectionChange(text, isIncomplete, window.Id, window.ProcessId, pointer.X, pointer.Y);
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        new LinuxNamedPipePeerVerifier().VerifyServer(pipe);
        await JsonSerializer.SerializeAsync(pipe, change, cancellationToken: deadline.Token).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        try
        {
            // Ignore the existing offer on enable. Watching must not resurrect a
            // stale selection merely because the preference or Host was restored.
            var baseline = await ReadBaselineAsync(_lifetime.Token).ConfigureAwait(false);
            var listener = ListenAsync(baseline);
            var start = new ProcessStartInfo("wl-paste")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The selection callback executable is unavailable.");
            foreach (var argument in new[]
                {
                    "--primary",
                    "--type",
                    "text",
                    "--watch",
                    executable,
                    "--linux-primary-selection",
                    _pipeName,
                }

                )
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the PRIMARY listener.");
                using var cancellation = _lifetime.Token.Register(() => Stop(process));
                // Drain diagnostics without exposing selection contents in application logs.
                var errors = process.StandardError.ReadToEndAsync(_lifetime.Token);
                await process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                if (!_lifetime.IsCancellationRequested)
                {
                    Log.Debug("The optional Wayland PRIMARY listener ended with code {ExitCode}.", process.ExitCode);
                }
            }
            finally
            {
                _lifetime.Cancel();
                await listener.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or TimeoutException or OperationCanceledException)
        {
            Log.Debug("Wayland PRIMARY observation is unavailable ({FailureType}); toolkit events remain active.", exception.GetType().Name);
        }
    }

    private async Task ListenAsync(string baseline)
    {
        var isInitialOffer = true;
        while (!_lifetime.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                new LinuxNamedPipePeerVerifier().VerifyClient(pipe);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                // A same-executable callback is trusted, but its payload still has
                // a finite size and deadline before JSON crosses the boundary.
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var buffer = new char[MaximumCharacters * 8];
                var count = await reader.ReadBlockAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (count == buffer.Length)
                {
                    continue;
                }

                var change = JsonSerializer.Deserialize<PrimarySelectionChange>(new string(buffer, 0, count));
                if (change is null || change.Text is null || change.Text.Length > MaximumCharacters)
                {
                    continue;
                }

                var fingerprint = Fingerprint(change.Text);
                var shouldIgnoreOffer = isInitialOffer && fingerprint == baseline;
                isInitialOffer = false;
                if (!shouldIgnoreOffer)
                {
                    _changed(change);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or OperationCanceledException or UnauthorizedAccessException)
            {
                if (_lifetime.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private static async Task<string> ReadBaselineAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("wl-paste")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "--primary",
                "--type",
                "text",
                "--no-newline",
            },
        };
        using var process = Process.Start(start) ?? throw new IOException("Could not read the initial PRIMARY offer.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        using var cancellation = deadline.Token.Register(() => Stop(process));
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        var (text, _) = await ReadTextAsync(process.StandardOutput, deadline.Token).ConfigureAwait(false);
        Stop(process);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await errors.ConfigureAwait(false);
        return Fingerprint(text);
    }

    private static async Task<(string Text, bool IsIncomplete)> ReadTextAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[MaximumCharacters + 1];
        var count = await reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
        var length = Math.Min(count, MaximumCharacters);
        if (length > 0 && char.IsHighSurrogate(buffer[length - 1]))
        {
            length--;
        }

        return (new string(buffer, 0, length), count > MaximumCharacters);
    }

    private static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }
}

/// <summary>
/// Text-only PRIMARY evidence, scoped to the foreground window observed by its callback.
/// </summary>
public sealed record PrimarySelectionChange(string Text, bool IsIncomplete, string WindowAddress, int ProcessId, int PointerX, int PointerY);