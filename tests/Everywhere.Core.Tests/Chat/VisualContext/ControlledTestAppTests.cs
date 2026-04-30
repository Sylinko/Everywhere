using System.Runtime.InteropServices;
using Everywhere.VisualContext.TestApp;

namespace Everywhere.Core.Tests.Chat.VisualContext;

public sealed class ControlledTestAppTests
{
    [TestCase("winforms")]
    [TestCase("avalonia")]
    [TestCase("cefsharp")]
    [Explicit("Launches visible controlled target processes and requires an interactive Windows desktop.")]
    [Platform("Win")]
    [Category("ControlledTestApp")]
    public async Task MoveNext_WhenControlledTargetIsReady_AdvancesOneRevision(string backend)
    {
        var executablePath = GetExecutablePath(backend);
        Assert.That(File.Exists(executablePath), Is.True, $"Build the '{backend}' TestApp before running this tier.");

        var (controller, ready) = await TestAppProcessController.StartAsync(
            executablePath,
            "chat",
            42,
            TimeSpan.FromSeconds(30));
        await using (controller)
        {
            await controller.SendAsync(new TestAppCommand(TestAppCommandKind.MoveNext));
            var advanced = await controller.ReadStatusAsync();

            Assert.Multiple(() =>
            {
                Assert.That(ready.Kind, Is.EqualTo(TestAppStatusKind.Ready));
                Assert.That(ready.ProcessId, Is.EqualTo(controller.Process.Id));
                Assert.That(ready.Roots, Has.Count.EqualTo(1));
                Assert.That(ready.Roots[0].NativeHandle, Is.Not.Zero);
                Assert.That(ready.Anchors, Is.Not.Empty);
                Assert.That(ready.Anchors[0].RootIndex, Is.Zero);
                Assert.That(ready.Anchors[0].NativeId, Is.Not.Empty);
                Assert.That(advanced.Kind, Is.EqualTo(TestAppStatusKind.Advanced));
                Assert.That(advanced.Step, Is.EqualTo(1));
                Assert.That(advanced.Revision, Is.EqualTo(1));
                Assert.That(advanced.Anchors, Is.Not.Empty);
            });
        }
    }

    private static string GetExecutablePath(string backend)
    {
        var repositoryRoot = FindRepositoryRoot();
        return backend switch
        {
            "winforms" => Path.Combine(
                repositoryRoot,
                "tests",
                "Everywhere.VisualContext.WinForms.TestApp",
                "bin",
                "Debug",
                "net10.0-windows",
                "Everywhere.VisualContext.WinForms.TestApp.exe"),
            "avalonia" => Path.Combine(
                repositoryRoot,
                "tests",
                "Everywhere.VisualContext.Avalonia.TestApp",
                "bin",
                "Debug",
                "net10.0",
                "Everywhere.VisualContext.Avalonia.TestApp.exe"),
            "cefsharp" => Path.Combine(
                repositoryRoot,
                "tests",
                "Everywhere.VisualContext.CefSharp.TestApp",
                "bin",
                "Debug",
                "net10.0-windows",
                GetRuntimeIdentifier(),
                "Everywhere.VisualContext.CefSharp.TestApp.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null),
        };
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Everywhere.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Everywhere repository root.");
    }

    private static string GetRuntimeIdentifier() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => "win-x86",
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => throw new PlatformNotSupportedException(
            $"CefSharp TestApp does not support {RuntimeInformation.ProcessArchitecture}."),
    };
}
