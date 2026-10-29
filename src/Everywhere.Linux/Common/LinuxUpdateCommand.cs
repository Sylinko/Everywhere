using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Everywhere.Common;

namespace Everywhere.Linux.Common;

/// <summary>
/// Runs the bundled package updater before any graphical application initialization.
/// </summary>
public static class LinuxUpdateCommand
{
    /// <summary>
    /// Executes an update using the application's saved settings; explicit arguments override defaults.
    /// </summary>
    public static async Task<int> RunAsync(string[] arguments)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "linux-update.py");
        if (!File.Exists(script))
        {
            Console.Error.WriteLine("The bundled updater is missing. Reinstall Everywhere from its trusted release source.");
            return 1;
        }

        try
        {
            var startInfo = new ProcessStartInfo("python3") { UseShellExecute = false };
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add("--repo");
            startInfo.ArgumentList.Add(LinuxUpdateHandler.ReleaseRepository);
            startInfo.Environment.Remove("EVERYWHERE_UPDATE_PROXY");
            startInfo.Environment.Remove("EVERYWHERE_UPDATE_CHANNEL");
            if (!arguments.Contains("--help") && !arguments.Contains("-h"))
            {
                var settings = LinuxUpdateSettings.Load(Path.Combine(RuntimeConstants.WritableFolderPath, "settings.json"),
                    shouldReadChannel: !arguments.Any(argument => argument == "--channel" || argument.StartsWith("--channel=", StringComparison.Ordinal)),
                    shouldReadProxy: !arguments.Any(argument => argument == "--proxy" || argument.StartsWith("--proxy=", StringComparison.Ordinal)));
                startInfo.Environment["EVERYWHERE_UPDATE_CHANNEL"] = settings.Channel.ToString().ToLowerInvariant();
            }
            // The helper reuses DynamicWebProxy, including SOCKS, authentication and bypass rules.
            // No proxy credentials are placed in process arguments or diagnostic output.
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running executable.");
            var transport = new List<string> { executable };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                transport.Add(typeof(Program).Assembly.Location);
            }
            transport.Add("--linux-update-download");
            startInfo.Environment["EVERYWHERE_UPDATE_TRANSPORT"] = JsonSerializer.Serialize(transport);
            if (!arguments.Contains("--dry-run"))
            {
                startInfo.ArgumentList.Add("--apply");
            }
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the updater.");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine("Could not start the updater. Check saved settings and ensure Python 3.9+ is installed.");
            return 1;
        }
    }
}