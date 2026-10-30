using System.Diagnostics;
using System.Globalization;
using Everywhere.Automation;
using Everywhere.Linux.Automation;
using Everywhere.Linux.Desktop;

namespace Everywhere.Linux.Hyprland;

/// <summary>
/// Uses grim's non-interactive screencopy path in the explicitly selected Hyprland session.
/// </summary>
public sealed class HyprlandSilentScreenCapturer(ICompositorStateProvider compositor) : ISilentScreenCapturer
{
    /// <inheritdoc />
    public async ValueTask<IVisualElementCapture> CaptureOutputAsync(string outputName, CancellationToken cancellationToken = default)
    {
        var outputs = await compositor.GetOutputsAsync(cancellationToken).ConfigureAwait(false);
        var output = outputs.FirstOrDefault(item => item.Name == outputName)
            ?? throw new NotSupportedException("The requested capture output is no longer active.");
        var start = new ProcessStartInfo("grim")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(output.Name);
        var scale = LinuxScreenCapture.OutputDensity(output);
        start.ArgumentList.Add("-s");
        start.ArgumentList.Add(scale.ToString("R", CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the screen capture helper.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var cancellation = timeout.Token.Register(() =>
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        });
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var png = new MemoryStream();
            var buffer = new byte[16384];
            while (true)
            {
                var count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                if (png.Length + count > 64 * 1024 * 1024)
                {
                    throw new InvalidDataException("The screenshot exceeded its buffer limit.");
                }

                png.Write(buffer, 0, count);
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Screen capture failed: " + await errors.ConfigureAwait(false));
            }

            return LinuxScreenCapture.Decode(png.ToArray(), output.Bounds);
        }
        finally
        {
            // An oversized reply or decoder failure must not leave a helper blocked
            // on stdout. Reap it before releasing the process and observe stderr.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
    }
}