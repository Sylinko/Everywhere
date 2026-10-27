using Everywhere.Interop;

namespace Everywhere.Linux.Tests;

[NonParallelizable]
public sealed class LinuxLauncherTests
{
    [TestCase("https://example.invalid/authorize?client_id=demo&state=a+b%2Fc&scope=openid%20profile#result", 0, true)]
    [TestCase("https://example.invalid/a?literal=$(touch%20unwanted)&other=1", 0, true)]
    [TestCase("https://example.invalid/repository", 4, false)]
    public async Task LaunchUri_WithQueryOrFailure_PreservesArgumentAndReportsExit(string target, int exitCode, bool isExpected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "everywhere-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var capturePath = Path.Combine(directory, "arguments.txt");
            var executable = Path.Combine(directory, "gio");
            await File.WriteAllTextAsync(executable,
                $"#!/bin/sh\nprintf '%s\\n' \"$#\" \"$1\" \"$2\" > '{capturePath}'\nexit {exitCode}\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("PATH", directory);
            var uri = new Uri(target);
            var isLaunched = await BetterBclLauncher.Shared.LaunchUriAsync(uri);
            Assert.That(isLaunched, Is.EqualTo(isExpected));
            Assert.That(await File.ReadAllLinesAsync(capturePath), Is.EqualTo(new[] { "2", "open", uri.AbsoluteUri }));
            Assert.That(File.Exists(Path.Combine(directory, "unwanted")), Is.False);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(directory, true);
        }
    }
}