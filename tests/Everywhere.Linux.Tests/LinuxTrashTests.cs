using Everywhere.Linux.Platform;

namespace Everywhere.Linux.Tests;

[NonParallelizable]
public sealed class LinuxTrashTests
{
    // The upstream migration requires recoverable deletion, never permanent fallback.
    [Test]
    public async Task ExistingFile_MoveToTrash_PreservesRecoverablePayload()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "everywhere-trash-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var previousVfs = Environment.GetEnvironmentVariable("GIO_USE_VFS");
        Directory.CreateDirectory(directory);
        try
        {
            // Keep asynchronous GVfs metadata services out of this local-file contract test.
            Environment.SetEnvironmentVariable("GIO_USE_VFS", "local");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(directory, "data"));
            var path = Path.Combine(directory, "payload with spaces.txt");
            await File.WriteAllTextAsync(path, "recoverable");
            await new LinuxNativeHelper().MoveToTrashAsync([path]);
            Assert.That(File.Exists(path), Is.False);
            var files = Directory.GetFiles(Path.Combine(directory, "data", "Trash", "files"));
            Assert.That(files, Has.Length.EqualTo(1));
            Assert.That(await File.ReadAllTextAsync(files[0]), Is.EqualTo("recoverable"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
            Environment.SetEnvironmentVariable("GIO_USE_VFS", previousVfs);
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task CanceledOperation_MoveToTrash_LeavesOriginalFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                new LinuxNativeHelper().MoveToTrashAsync([path], cancellation.Token));
            Assert.That(File.Exists(path), Is.True);
        }
        finally
        {
            File.Delete(path);
        }
    }
}