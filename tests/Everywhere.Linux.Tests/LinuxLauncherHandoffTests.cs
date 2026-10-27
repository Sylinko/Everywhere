using System.Diagnostics;
using Everywhere.Interop;

namespace Everywhere.Linux.Tests;

[NonParallelizable]
public sealed class LinuxLauncherHandoffTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task LaunchUri_WithIsolatedAssociation_ReportsHandoffWithoutWaiting(bool hasRegisteredHandler)
    {
        var directory = Path.Combine(Path.GetTempPath(), "everywhere launcher handoff " + Guid.NewGuid().ToString("N"));
        var variables = new Dictionary<string, string>
        {
            ["XDG_DATA_HOME"] = Path.Combine(directory, "data"),
            ["XDG_CONFIG_HOME"] = Path.Combine(directory, "config"),
            ["XDG_DATA_DIRS"] = Path.Combine(directory, "system-data"),
            ["XDG_CONFIG_DIRS"] = Path.Combine(directory, "system-config")
        };
        var previousValues = variables.ToDictionary(pair => pair.Key, pair => Environment.GetEnvironmentVariable(pair.Key));
        var capturePath = Path.Combine(directory, "handler.txt");
        var applications = Path.Combine(variables["XDG_DATA_HOME"], "applications");
        Directory.CreateDirectory(applications);
        Directory.CreateDirectory(variables["XDG_CONFIG_HOME"]);

        try
        {
            var handlerPath = Path.Combine(directory, "handler \"\\$`%.sh");
            await File.WriteAllTextAsync(handlerPath,
                $"#!/bin/sh\nprintf '%s\\n' \"$$\" \"$1\" > '{capturePath}.pending'\n/bin/mv '{capturePath}.pending' '{capturePath}'\nexec /bin/sleep 120\n");
            if (hasRegisteredHandler)
            {
                // Desktop entry strings are unescaped before Exec arguments are parsed.
                var escapedHandlerPath = handlerPath.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%");
                var handlerArgument = "\"" + escapedHandlerPath.Replace("\\", "\\\\")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
                await File.WriteAllTextAsync(Path.Combine(applications, "everywhere-launcher-test.desktop"),
                    $"[Desktop Entry]\nType=Application\nName=Everywhere launcher test\nExec=/bin/sh {handlerArgument} %u\nTerminal=false\nMimeType=x-scheme-handler/everywhere-launcher-test;\n");
            }

            await File.WriteAllTextAsync(Path.Combine(variables["XDG_CONFIG_HOME"], "mimeapps.list"),
                "[Default Applications]\nx-scheme-handler/everywhere-launcher-test=everywhere-launcher-test.desktop;\n");
            foreach (var pair in variables)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            var uri = new Uri("everywhere-launcher-test://handoff?first=a%20b&second=2");
            var isLaunched = await BetterBclLauncher.Shared.LaunchUriAsync(uri).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(isLaunched, Is.EqualTo(hasRegisteredHandler));
            if (!hasRegisteredHandler)
            {
                return;
            }

            // GIO can acknowledge spawning before the handler writes its capture.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(capturePath))
            {
                await Task.Delay(20, timeout.Token);
            }

            var captured = await File.ReadAllLinesAsync(capturePath);
            using var handler = Process.GetProcessById(int.Parse(captured[0]));
            Assert.That(captured[1], Is.EqualTo(uri.AbsoluteUri));
            Assert.That(handler.HasExited, Is.False);
        }
        finally
        {
            foreach (var pair in previousValues)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            if (File.Exists(capturePath))
            {
                var captured = await File.ReadAllLinesAsync(capturePath);
                using var handler = Process.GetProcessById(int.Parse(captured[0]));
                if (!handler.HasExited)
                {
                    // GIO owns this process; container init may not reap it immediately.
                    handler.Kill();
                }
            }

            Directory.Delete(directory, true);
        }
    }
}