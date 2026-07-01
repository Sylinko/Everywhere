using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DynamicData;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.I18N;
using Lucide.Avalonia;
using Microsoft.SemanticKernel;

namespace Everywhere.Linux.Chat.Plugins;

/// <summary>
/// A plugin that integrates with the `fd` (fd-find) command-line tool to provide fast file search on Linux.
/// </summary>
public sealed class FdFindPlugin : BuiltInChatPlugin
{
    public override IDynamicLocaleKey HeaderKey { get; } = new DynamicLocaleKey(LocaleKey.Linux_BuiltInChatPlugin_FdFind_Header);
    public override IDynamicLocaleKey DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.Linux_BuiltInChatPlugin_FdFind_Description);
    public override LucideIconKind? Icon => LucideIconKind.Search;
    public override bool IsDefaultEnabled => false;

    private string? _detectedFdCommand;

    public FdFindPlugin() : base("fdfind")
    {
        _functionsSource.Add(
            new BuiltInChatFunction(
                SearchFilesAsync,
                ChatFunctionPermissions.FileRead));
    }

    /// <summary>
    /// Ensure that fd command is installed and return the command name.
    /// </summary>
    private async ValueTask<string> EnsureFdInstalledAsync()
    {
        if (_detectedFdCommand != null) return _detectedFdCommand;

        // fd command differs by distribution: "fd" or "fdfind"
        foreach (var cmd in new[] { "fd", "fdfind" })
        {
            if (await CheckCommandExists(cmd))
            {
                _detectedFdCommand = cmd;
                return cmd;
            }
        }

        throw new InvalidOperationException(
            "The 'fd' or 'fdfind' tool is not installed. Please install it using your package manager (e.g., 'sudo apt install fd-find').");
    }

    private static async Task<bool> CheckCommandExists(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = command,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [KernelFunction("search_files")]
    [Description("Search for files and directories in a specified path using fd-find. Highly efficient for large file systems.")]
    [DynamicLocaleKey(LocaleKey.Linux_BuiltInChatPlugin_FdFind_SearchFiles_Header)]
    private async Task<string> SearchFilesAsync(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [Description("The root directory to start searching from.")] string path,
        [Description("Regex search pattern to match file and directory names.")] string filePattern = ".*",
        [Description("Maximum number of results to return. Max is 1000.")] int maxCount = 100,
        FilesOrderBy orderBy = FilesOrderBy.Default,
        CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "This plugin is only supported on Linux.";
        }
        maxCount = Math.Clamp(maxCount, 0, 1000);

        string fdCmd = await EnsureFdInstalledAsync();
        var startInfo = new ProcessStartInfo
        {
            FileName = fdCmd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--absolute-path");
        startInfo.ArgumentList.Add("--hidden");
        startInfo.ArgumentList.Add("--ignore-case");
        startInfo.ArgumentList.Add("--max-results");
        startInfo.ArgumentList.Add(maxCount.ToString());
        startInfo.ArgumentList.Add(filePattern);
        startInfo.ArgumentList.Add(path);

        return await Task.Run(async () =>
        {
            var results = new List<FileRecord>();

            using var process = Process.Start(startInfo);
            if (process == null) return "Failed to start fd process.";

            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (!string.IsNullOrEmpty(line))
            {
                results.Add(CreateFileRecordFromPath(line));
            }

            await process.WaitForExitAsync(cancellationToken);
            IEnumerable<FileRecord> query = results;
            query = orderBy switch
            {
                FilesOrderBy.Name => query.OrderBy(item => item.FullPath),
                FilesOrderBy.Size => query.OrderBy(item => item.BytesSize),
                FilesOrderBy.Created => query.OrderBy(item => item.Created),
                FilesOrderBy.LastModified => query.OrderBy(item => item.Modified),
                _ => query
            };

            var finalResults = query.Take(maxCount).ToList();

            displaySink.AppendDynamicLocaleKey(
                new FormattedDynamicLocaleKey(
                    LocaleKey.Linux_BuiltInChatPlugin_FdFind_SearchFiles_DetailMessage,
                    new DirectLocaleKey(finalResults.Count.ToString())));

            return new FileRecords(finalResults, finalResults.Count).ToString();
        }, cancellationToken);
    }

    private static FileRecord CreateFileRecordFromPath(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                return new FileRecord(
                    path,
                    info.Length,
                    info.CreationTimeUtc,
                    info.LastWriteTimeUtc,
                    info.Attributes);
            }
            
            var dirInfo = new DirectoryInfo(path);
            return new FileRecord(
                path,
                -1,
                dirInfo.CreationTimeUtc,
                dirInfo.LastWriteTimeUtc,
                dirInfo.Attributes);
        }
        catch
        {
            return new FileRecord(path, -1, null, null, FileAttributes.None);
        }
    }
}