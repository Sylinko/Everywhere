using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Everywhere.Windows.Common;

namespace Everywhere.Windows.Tests.Common;

[Platform("Win")]
public sealed class WindowsUpdateHandlerTests
{
    [Test]
    public async Task PreparePortableUpdateAsync_WithValidArchive_StagesExecutableScriptAndParentProcessId()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"everywhere-update-test-{Guid.NewGuid():N}");
        var appDir = Path.Combine(tempRoot, "app");
        var executablePath = Path.Combine(appDir, "Everywhere.exe");
        var zipPath = Path.Combine(tempRoot, "Everywhere-Windows-x64-v1.2.3.zip");
        Directory.CreateDirectory(appDir);

        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("Everywhere.exe");
                await using var stream = entry.Open();
                await stream.WriteAsync("stub"u8.ToArray());
            }

            var startInfo = await PreparePortableUpdateAsync(null, zipPath, executablePath, 4242, CancellationToken.None);
            var stagedWorkspace = startInfo.WorkingDirectory;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(startInfo.FileName, Does.EndWith("powershell.exe").IgnoreCase);
                Assert.That(Directory.Exists(stagedWorkspace), Is.True);
                Assert.That(File.Exists(Path.Combine(stagedWorkspace, "new", "Everywhere.exe")), Is.True);
                Assert.That(File.Exists(Path.Combine(stagedWorkspace, "update.ps1")), Is.True);
                Assert.That(startInfo.ArgumentList, Does.Contain("-ParentProcessId"));
                var pidFlagIndex = startInfo.ArgumentList.IndexOf("-ParentProcessId");
                Assert.That(pidFlagIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(startInfo.ArgumentList[pidFlagIndex + 1], Is.EqualTo("4242"));
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task PreparePortableUpdateAsync_WhenArchiveLacksRootExecutable_DeletesStagingDirectoryAndThrows()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"everywhere-update-test-{Guid.NewGuid():N}");
        var appDir = Path.Combine(tempRoot, "app");
        var executablePath = Path.Combine(appDir, "Everywhere.exe");
        var zipPath = Path.Combine(tempRoot, "Everywhere-Windows-x64-v1.2.3.zip");
        Directory.CreateDirectory(appDir);

        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("other.txt");
                await using var stream = entry.Open();
                await stream.WriteAsync("stub"u8.ToArray());
            }

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await PreparePortableUpdateAsync(null, zipPath, executablePath, 4242, CancellationToken.None));

            Assert.That(Directory.GetDirectories(tempRoot, ".Everywhere-update-*"), Is.Empty);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "PreparePortableUpdateAsync")]
    private static extern Task<ProcessStartInfo> PreparePortableUpdateAsync(
        WindowsUpdateHandler? _,
        string zipPath,
        string executablePath,
        int parentProcessId,
        CancellationToken cancellationToken);
}
