using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Everywhere.VisualContext.TestApp;

/// <summary>
/// Identifies messages emitted by a controlled visual-context TestApp.
/// </summary>
public enum TestAppStatusKind
{
    /// <summary>
    /// The target windows and their accessibility surfaces are ready for inspection.
    /// </summary>
    Ready,

    /// <summary>
    /// A deterministic mutation step completed.
    /// </summary>
    Advanced,

    /// <summary>
    /// The target encountered an error that prevents the requested operation.
    /// </summary>
    Error,
}

/// <summary>
/// Identifies commands accepted through a TestApp's standard-input control channel.
/// </summary>
public enum TestAppCommandKind
{
    /// <summary>
    /// Advances the deterministic scenario step once.
    /// </summary>
    MoveNext,

    /// <summary>
    /// Requests an orderly target-process shutdown.
    /// </summary>
    Stop,
}

/// <summary>
/// Describes one top-level root reported by a controlled target process.
/// </summary>
public sealed record TestAppRootStatus(int Index, long NativeHandle);

/// <summary>
/// Identifies one declarative core anchor that a production platform reader can resolve inside a reported root.
/// </summary>
public sealed record TestAppAnchorStatus(int RootIndex, string Path, string? Key, string NativeId);

/// <summary>
/// Carries a target-process state transition as one JSON line on standard output.
/// </summary>
public sealed record TestAppStatus(
    TestAppStatusKind Kind,
    string Scenario,
    long Seed,
    long Step,
    long Revision,
    int ProcessId,
    IReadOnlyList<TestAppRootStatus> Roots,
    IReadOnlyList<TestAppAnchorStatus> Anchors,
    string? Error = null);

/// <summary>
/// Carries one controller request as a JSON line on standard input.
/// </summary>
public sealed record TestAppCommand(TestAppCommandKind Kind);

/// <summary>
/// Serializes the revision protocol shared by all controlled TestApps and their process controller.
/// </summary>
public static class TestAppProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Serializes one protocol value to a compact JSON line payload.
    /// </summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>
    /// Deserializes one JSON line payload and requires a non-null result.
    /// </summary>
    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new JsonException($"The TestApp protocol returned no {typeof(T).Name} value.");
}

/// <summary>
/// Owns a controlled TestApp process and its deterministic JSON-lines command channel.
/// </summary>
/// <remarks>
/// The controller terminates only the exact process it launched. Disposing the controller never
/// searches for or kills other processes by name.
/// </remarks>
public sealed class TestAppProcessController : IAsyncDisposable
{
    /// <summary>
    /// Gets the exact controlled process owned by this controller.
    /// </summary>
    public Process Process { get; }

    private bool _disposed;

    private TestAppProcessController(Process process) => Process = process;

    /// <summary>
    /// Starts a TestApp and waits for its first ready status within the supplied timeout.
    /// </summary>
    public static async Task<(TestAppProcessController Controller, TestAppStatus ReadyStatus)> StartAsync(
        string executablePath,
        string scenario,
        long seed,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startupTimeout, TimeSpan.Zero);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--scenario");
        startInfo.ArgumentList.Add(scenario);
        startInfo.ArgumentList.Add("--seed");
        startInfo.ArgumentList.Add(seed.ToString(CultureInfo.InvariantCulture));

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start TestApp '{executablePath}'.");
        var controller = new TestAppProcessController(process);

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(startupTimeout);
            var status = await controller.ReadStatusAsync(timeoutSource.Token).ConfigureAwait(false);
            if (status.Kind != TestAppStatusKind.Ready)
            {
                throw new InvalidOperationException(
                    $"TestApp '{executablePath}' returned {status.Kind} before it became ready: {status.Error}");
            }

            return (controller, status);
        }
        catch
        {
            await controller.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Sends one deterministic control command to the target process.
    /// </summary>
    public async Task SendAsync(TestAppCommand command, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Process.StandardInput.WriteLineAsync(TestAppProtocol.Serialize(command).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await Process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads and parses the next target status line.
    /// </summary>
    public async Task<TestAppStatus> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var line = await Process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is not null)
        {
            return TestAppProtocol.Deserialize<TestAppStatus>(line);
        }

        var error = await Process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        throw new EndOfStreamException(
            $"TestApp process {Process.Id} ended before reporting status. Standard error: {error}");
    }

    /// <summary>
    /// Requests orderly shutdown, then terminates the owned process tree if it does not exit promptly.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!Process.HasExited)
        {
            try
            {
                await Process.StandardInput.WriteLineAsync(
                        TestAppProtocol.Serialize(new TestAppCommand(TestAppCommandKind.Stop)))
                    .ConfigureAwait(false);
                await Process.StandardInput.FlushAsync().ConfigureAwait(false);

                using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }

        Process.Dispose();
    }
}
