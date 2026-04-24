using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using Everywhere.Chat.Permissions;
using Everywhere.Common;
using Everywhere.Utilities;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat.Plugins.BuiltIn;

public sealed class FileSystemPlugin : BuiltInChatPlugin
{
    private static TimeSpan RegexTimeout => TimeSpan.FromSeconds(3);

    public override IDynamicResourceKey HeaderKey { get; } = new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_Header);
    public override IDynamicResourceKey DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_Description);
    public override LucideIconKind? Icon => LucideIconKind.FileBox;

    private readonly ILogger<FileSystemPlugin> _logger;

    public FileSystemPlugin(ILogger<FileSystemPlugin> logger) : base("file_system")
    {
        _logger = logger;

        _functionsSource.Edit(list =>
        {
            list.Add(
                new BuiltInChatFunction(
                    SearchFiles,
                    ChatFunctionPermissions.FileRead));
            list.Add(
                new BuiltInChatFunction(
                    GetFileInformation,
                    ChatFunctionPermissions.FileRead));
            list.Add(
                new BuiltInChatFunction(
                    SearchFileContentAsync,
                    ChatFunctionPermissions.FileRead));
            list.Add(
                new BuiltInChatFunction(
                    ReadFileAsync,
                    ChatFunctionPermissions.FileRead));
            list.Add(
                new BuiltInChatFunction(
                    MoveFile,
                    ChatFunctionPermissions.FileAccess));
            list.Add(
                new BuiltInChatFunction(
                    DeleteFilesAsync,
                    ChatFunctionPermissions.FileAccess));
            list.Add(
                new BuiltInChatFunction(
                    CreateDirectory,
                    ChatFunctionPermissions.FileAccess));
            list.Add(
                new BuiltInChatFunction(
                    WriteToFileAsync,
                    ChatFunctionPermissions.FileAccess));
            list.Add(
                new BuiltInChatFunction(
                    ReplaceFileContentAsync,
                    ChatFunctionPermissions.FileAccess));
        });
    }

    // parts of algorithms for file searching are inspired by VS Code's implementation:
    // https://github.com/microsoft/vscode/tree/dc1de9b2cf2defca5e4fcfa120a7cf348e57b55b/extensions/copilot/src/extension/tools/node/findFilesTool.tsx
    [KernelFunction("search_files")]
    [Description(
        """
        Search for files and directories in a specified path matching the given regex pattern.
        - Automatically ignores common build/hidden folders (e.g., bin, obj, .git, node_modules).
        - Has a 20-second timeout to prevent hanging.
        """)]
    [DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_SearchFiles_Header, LocaleKey.BuiltInChatPlugin_FileSystem_SearchFiles_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private string SearchFiles(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [FromKernelServices] ChatContext chatContext,
        string path,
        [Description("Regex search pattern to match file and directory names.")] string filePattern = ".*",
        int skip = 0,
        [Description("Maximum number of results to return. Max is 1000.")] int maxCount = 100,
        CancellationToken cancellationToken = default)
    {
        skip = Math.Max(0, skip);
        maxCount = Math.Clamp(maxCount, 0, 1000);

        _logger.LogDebug(
            "Searching files in path: {Path} with pattern: {SearchPattern}, skip: {Skip}, maxCount: {MaxCount}",
            path,
            filePattern,
            skip,
            maxCount);

        ExpandFullPath(chatContext, ref path);
        displaySink.AppendFileReferences(new ChatPluginFileReference(path));

        var regex = new Regex(filePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
        var directoryInfo = EnsureDirectoryInfo(path);

        var fileReferences = new List<ChatPluginFileReference>();
        var results = new List<string>();
        var totalResults = 0;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            foreach (var info in new RegexFileSystemInfoEnumerable(directoryInfo.FullName, regex, true, ignoreCommonBuildFolders: true))
            {
                cts.Token.ThrowIfCancellationRequested();
                if (info == null) continue;

                totalResults++;
                if (totalResults <= skip) continue;

                if (results.Count < maxCount)
                {
                    results.Add(info.FullName);
                    fileReferences.Add(new ChatPluginFileReference(info.FullName));
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (results.Count == 0)
            {
                return "Search timed out after 20 seconds. No files found.";
            }
        }

        if (results.Count == 0)
        {
            return "No files found.";
        }

        displaySink.AppendFileReferences(fileReferences.ToArray());

        var sb = new StringBuilder();
        sb.Append(totalResults).AppendLine(totalResults == 1 ? " total result" : " total results");

        // TODO: Implement TextChunk and priority-based truncation logic from VS Code.
        // VS Code uses <TextChunk priority="10"> for file paths and <TextChunk priority="20"> for total results and ellipsis.
        foreach (var result in results)
        {
            sb.AppendLine(result);
        }

        if (totalResults > skip + results.Count)
        {
            sb.AppendLine("...");
        }

        return sb.ToString();
    }

    [KernelFunction("get_file_info")]
    [Description("Get information about a file or directory at the specified path.")]
    [DynamicResourceKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_GetFileInformation_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_GetFileInformation_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private string GetFileInformation(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [FromKernelServices] ChatContext chatContext,
        string path)
    {
        _logger.LogDebug("Getting file information for path: {Path}", path);

        ExpandFullPath(chatContext, ref path);
        displaySink.AppendFileReferences(new ChatPluginFileReference(path));

        var info = EnsureFileSystemInfo(path);
        var sb = new StringBuilder();
        return sb.AppendLine(FileRecord.Header).Append(
            new FileRecord(
                info.FullName,
                info is FileInfo file ? file.Length : -1,
                info.CreationTime,
                info.LastWriteTime,
                info.Attributes)).ToString();
    }

    // parts of algorithms for file content searching are inspired by VS Code's implementation:
    // https://github.com/microsoft/vscode/tree/dc1de9b2cf2defca5e4fcfa120a7cf348e57b55b/extensions/copilot/src/extension/tools/node/findTextInFilesTool.tsx
    [KernelFunction("search_file_content")]
    [Description(
        """
        Searches for a specific text pattern within file(s) and returns matching lines. Maximum returns 1000 lines.
        - Supports both regex and literal text search (toggle `isRegex`).
        - Automatically ignores common build/hidden folders (e.g., bin, obj, .git, node_modules).
        - Has a 20-second timeout to prevent hanging.
        - Truncates extremely long lines around the match to save tokens.
        """)]
    [DynamicResourceKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_SearchFileContent_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_SearchFileContent_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task<string> SearchFileContentAsync(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [FromKernelServices] ChatContext chatContext,
        [Description("File or directory path to search.")] string path,
        [Description("Text or regex pattern to search for within the file.")] string pattern,
        [Description("Whether the pattern is a regular expression. Set to false for literal text search.")]
        bool isRegex = true,
        bool ignoreCase = true,
        [Description("Regex pattern to include files to search. Effective when path is a folder.")]
        string filePattern = ".*",
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Searching file content in path: {Path} with pattern: {SearchPattern}, isRegex: {IsRegex}, ignoreCase: {IgnoreCase}, filePattern: {FilePattern}",
            path,
            pattern,
            isRegex,
            ignoreCase,
            filePattern);

        ExpandFullPath(chatContext, ref path);
        displaySink.AppendFileReferences(new ChatPluginFileReference(path));

        var regexOptions = RegexOptions.Compiled | RegexOptions.Multiline;
        if (ignoreCase)
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }

        var actualPattern = isRegex ? pattern : Regex.Escape(pattern);
        var searchRegex = new Regex(actualPattern, regexOptions, RegexTimeout);
        var fileRegex = new Regex(filePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var fileSystemInfo = EnsureFileSystemInfo(path);

        const int maxCollectedLines = 2000;
        const int maxCharsBetweenMatches = 500;
        const long maxSearchFileSize = 10 * 1024 * 1024; // 10 MB

        var filesToSearch = fileSystemInfo switch
        {
            FileInfo fileInfo when fileRegex.IsMatch(fileInfo.Name) => [fileInfo],
            DirectoryInfo directoryInfo => new RegexFileSystemInfoEnumerable(directoryInfo.FullName, fileRegex, true, ignoreCommonBuildFolders: true)
                .WithCancellation(cancellationToken)
                .OfType<FileInfo>(),
            _ => []
        };
        var matchCount = 0;
        var resultLines = new ConcurrentBag<string>();
        var fileReferences = new ConcurrentBag<ChatPluginFileReference>();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            await Parallel.ForEachAsync(
                filesToSearch,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = cts.Token
                },
                async (file, token) =>
                {
                    if (Volatile.Read(ref matchCount) >= maxCollectedLines) return;
                    if (file.Length > maxSearchFileSize) return;

                    await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: token) is not { } encoding)
                    {
                        return;
                    }

                    stream.Seek(0, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
                    var content = await reader.ReadToEndAsync(token);

                    var matches = searchRegex.Matches(content);
                    if (matches.Count == 0) return;

                    var localResults = new List<string>();
                    var locations = new HashSet<ChatPluginFileReference.Location>();

                    foreach (Match match in matches)
                    {
                        if (Interlocked.Increment(ref matchCount) > maxCollectedLines) break;

                        var lineStart = content.LastIndexOf('\n', match.Index) + 1;
                        var lineNumber = content.AsSpan(0, match.Index).Count('\n') + 1;
                        var columnNumber = match.Index - lineStart + 1;

                        locations.Add(new ChatPluginFileReference.Location(lineNumber, columnNumber));

                        var start = Math.Max(0, match.Index - maxCharsBetweenMatches);
                        var end = Math.Min(content.Length, match.Index + match.Length + maxCharsBetweenMatches);
                        var previewSpan = content.AsSpan(start, end - start);
                        var lineCount = 0;
                        foreach (var _ in previewSpan.EnumerateLines())
                        {
                            lineCount++;
                        }

                        var matchBuilder = new StringBuilder();
                        matchBuilder.Append("<match path=\"").Append(file.FullName).Append("\" line=\"").Append(lineNumber).AppendLine("\">");

                        // TODO: Implement TextChunk and priority-based truncation logic similar to VS Code.
                        // VS Code calculates priority as: var priority = 1000 - Math.Abs(i - center);
                        // where center is the middle line of the preview block.
                        // Each line should be wrapped in <TextChunk priority="{priority}">{line}</TextChunk>
                        var i = 0;
                        foreach (var line in previewSpan.EnumerateLines())
                        {
                            if (i == 0 && start > 0)
                            {
                                matchBuilder.Append("...");
                            }

                            matchBuilder.Append(line);

                            if (i == lineCount - 1 && end < content.Length)
                            {
                                matchBuilder.Append("...");
                            }

                            matchBuilder.AppendLine();
                            i++;
                        }

                        matchBuilder.AppendLine("</match>");
                        localResults.Add(matchBuilder.ToString());
                    }

                    if (localResults.Count > 0)
                    {
                        fileReferences.Add(new ChatPluginFileReference(file.FullName, locations: locations));
                        resultLines.Add(string.Join("\n", localResults));
                    }
                });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (resultLines.IsEmpty)
            {
                return "Search timed out after 20 seconds. Found 0 files with matches so far. Try a more specific search pattern or path.";
            }

            displaySink.AppendFileReferences(fileReferences.ToArray());
            var timeoutMessageBuilder = new StringBuilder();
            timeoutMessageBuilder.Append("Search timed out after 20 seconds. Found ").Append(resultLines.Count)
                .AppendLine(" files with matches so far. Try a more specific search pattern or path.");
            foreach (var line in resultLines.Take(maxCollectedLines)) timeoutMessageBuilder.AppendLine(line);
            return timeoutMessageBuilder.ToString();
        }

        if (resultLines.IsEmpty)
        {
            return
                $"""
                 No matching lines found for {(isRegex ? "regex" : "literal text")} '{pattern}'.
                 Hint: If you expect results, check if the files are ignored (e.g., in bin/obj/.git), or try toggling the 'isRegex' parameter, or use a different pattern.
                 """;
        }

        displaySink.AppendFileReferences(fileReferences.ToArray());
        var resultBuilder = new StringBuilder();
        resultBuilder.Append("Found matches in ").Append(resultLines.Count).AppendLine(" file(s).");
        foreach (var resultLine in resultLines.Take(maxCollectedLines)) resultBuilder.AppendLine(resultLine);
        return resultBuilder.ToString();
    }

    [KernelFunction("read_file")]
    [Description(
        """
        Read the contents of a file. Line numbers are 1-indexed. 
        This tool will truncate its output at 2000 lines and may be called repeatedly with offset and limit parameters to read larger files in chunks. 
        Binary files use offset/limit as byte offsets.
        """)]
    [DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_Header, LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task<string> ReadFileAsync(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [FromKernelServices] ChatContext chatContext,
        string path,
        [Description("Optional: the 1-based line number or bytes to start reading from. If not specified, reads from the beginning.")]
        int offset = 1,
        [Description("Optional: the maximum number of lines/bytes to read.")] int limit = 2000,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Reading text file at path: {Path}, offset: {Offset}, limit: {Limit}",
            path,
            offset,
            limit);

        ExpandFullPath(chatContext, ref path);
        displaySink.AppendFileReferences(new ChatPluginFileReference(path));

        var fileInfo = EnsureFileInfo(path);
        if (fileInfo.Length > 100 * 1024 * 1024)
        {
            throw new HandledException(
                new NotSupportedException("File size is larger than 100 MB, read operation is not supported."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_FileTooLarge_ErrorMessage),
                showDetails: false);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fileInfo.Length == 0)
        {
            return $"(The file `{path}` exists, but is empty)";
        }

        if (await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken) is not { } encoding)
        {
            // Binary file logic
            long startByte = Math.Max(0, offset - 1);
            long maxBytes = limit == 2000 ? 10240 : limit;

            stream.Seek(startByte, SeekOrigin.Begin);

            var buffer = new byte[32];
            int bytesRead;
            var stringBuilder = new StringBuilder();

            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                var hexString = BitConverter.ToString(buffer, 0, bytesRead);
                stringBuilder.AppendLine(hexString);

                if (stringBuilder.Length >= maxBytes)
                {
                    break;
                }
            }

            return $"Binary file {path} (Bytes {startByte} to {stream.Position}):\n{stringBuilder}";
        }

        // Text file logic
        var startLine = Math.Max(1, offset);
        var actualLimit = Math.Clamp(limit, 1, 2000);

        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);

        var contentBuilder = new StringBuilder();
        var currentLine = 0;
        var linesRead = 0;
        var isEOF = false;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            currentLine++;

            if (currentLine < startLine) continue;

            contentBuilder.AppendLine(line);
            linesRead++;

            if (linesRead >= actualLimit)
            {
                isEOF = await reader.ReadLineAsync(cancellationToken) == null;
                break;
            }
        }

        if (linesRead == 0 && currentLine == 0)
        {
            return $"(The file `{path}` exists, but is empty)";
        }

        if (string.IsNullOrWhiteSpace(contentBuilder.ToString()))
        {
            return $"(The file `{path}` exists, but contains only whitespace)";
        }

        var endLine = startLine + linesRead - 1;
        var truncated = !isEOF && linesRead == actualLimit;

        // Calculate total lines if the file is not too large
        int? totalLines = null;
        if (fileInfo.Length < 1024 * 1024)
        {
            totalLines = currentLine;
            if (!isEOF)
            {
                while (await reader.ReadLineAsync(cancellationToken) != null)
                {
                    totalLines++;
                }
            }
        }

        var resultBuilder = new StringBuilder();
        if (truncated || startLine > 1)
        {
            resultBuilder.Append("File: `").Append(path).Append("`. Lines ").Append(startLine).Append(" to ").Append(endLine);
            if (totalLines.HasValue) resultBuilder.Append(" (").Append(totalLines.Value).Append(" lines total)");
            else resultBuilder.Append(" (total lines unknown because file is too large: ").Append(fileInfo.Length).Append(" bytes)");
            resultBuilder.AppendLine(":");
        }

        resultBuilder.Append(contentBuilder);

        if (truncated)
        {
            resultBuilder
                .AppendLine()
                .Append("[File content truncated at line ").Append(endLine)
                .AppendLine(". Use read_file with offset/limit parameters to view more.]");
        }

        return resultBuilder.ToString();
    }

    [KernelFunction("move_file")]
    [Description("Moves or renames a file or directory.")]
    [DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_MoveFile_Header, LocaleKey.BuiltInChatPlugin_FileSystem_MoveFile_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private bool MoveFile(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [FromKernelServices] ChatContext chatContext,
        [Description("Source file or directory path.")] string source,
        [Description("Destination file or directory path. Type must match the source.")] string destination)
    {
        _logger.LogDebug("Moving file from {Source} to {Destination}", source, destination);

        ExpandFullPath(chatContext, ref source);
        ExpandFullPath(chatContext, ref destination);
        displaySink.AppendFileReferences(
            new ChatPluginFileReference(source),
            new ChatPluginFileReference(destination));

        var isFile = File.Exists(source);
        if (!isFile && !Directory.Exists(source))
        {
            throw new HandledSystemException(
                new FileNotFoundException($"{nameof(source)} does not exist."),
                HandledSystemExceptionType.FileNotFound);
        }

        var destinationDirectory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new HandledSystemException(
                new DirectoryNotFoundException($"{nameof(destination)} directory is invalid."),
                HandledSystemExceptionType.DirectoryNotFound);
        }

        try
        {
            Directory.CreateDirectory(destinationDirectory);
        }
        catch (Exception ex)
        {
            throw new HandledSystemException(
                new IOException("Failed to create destination directory.", ex),
                HandledSystemExceptionType.IOException,
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_MoveFile_CreateDirectory_ErrorMessage));
        }

        if (isFile)
        {
            File.Move(source, destination, overwrite: false);
        }
        else
        {
            Directory.Move(source, destination);
        }

        return true;
    }

    [KernelFunction("delete_files")]
    [Description(
        "Delete files and directories at the specified path matching the given pattern.")]
    [DynamicResourceKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task<string> DeleteFilesAsync(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        [Description("File or directory path to delete.")] string path,
        [Description(
            "Regex search pattern to match file and directory names (not full path). " +
            "Effective when path is a folder. " +
            "Warn that this will delete all matching files and directories recursively.")]
        string filePattern = ".*",
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting file at {Path}", path);

        ExpandFullPath(chatContext, ref path);
        displaySink.AppendFileReferences(new ChatPluginFileReference(path));

        if (Path.GetDirectoryName(path) is null)
        {
            throw new HandledException(
                new UnauthorizedAccessException("Cannot delete root directory."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_RootDirectory_Deletion_ErrorMessage),
                showDetails: false);
        }

        var fileSystemInfo = EnsureFileSystemInfo(path);
        if (fileSystemInfo.Attributes.HasFlag(FileAttributes.System))
        {
            throw new HandledException(
                new UnauthorizedAccessException("Cannot delete system files or directories."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_SystemFile_Deletion_ErrorMessage),
                showDetails: false);
        }

        IEnumerable<FileSystemInfo> infosToDelete;
        switch (fileSystemInfo)
        {
            case FileInfo fileInfo:
            {
                infosToDelete = [fileInfo];
                break;
            }
            case DirectoryInfo directoryInfo:
            {
                var regex = new Regex(filePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
                infosToDelete = new RegexFileSystemInfoEnumerable(directoryInfo.FullName, regex, true)
                    .WithCancellation(cancellationToken)
                    .OfType<FileSystemInfo>();
                break;
            }
            default:
            {
                return "No files or directories to delete.";
            }
        }

        var successCount = 0;
        var errorCount = 0;
        foreach (var info in infosToDelete)
        {
            if (info.Attributes.HasFlag(FileAttributes.System))
            {
                var consent = await userInterface.RequestConsentAsync(
                    "system",
                    new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_SystemFile_DeletionConsent_Header),
                    new ChatPluginFileReferencesDisplayBlock(new ChatPluginFileReference(info.FullName)),
                    cancellationToken: cancellationToken);
                if (!consent)
                {
                    continue;
                }
            }

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return $"User cancelled the deletion operation. " +
                        $"{successCount} files/directories were deleted successfully, {errorCount} errors occurred.";
                }

                if (info.Exists)
                {
                    if (info is DirectoryInfo directoryInfo) directoryInfo.Delete(true);
                    else info.Delete();
                }

                successCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete file or directory at {Path}", info.FullName);

                errorCount++;
            }
        }

        return errorCount == 0 ?
            $"{successCount} files/directories were deleted successfully." :
            $"{successCount} files/directories were deleted successfully, {errorCount} errors occurred.";
    }

    [KernelFunction("create_directory")]
    [Description("Creates a new directory at the specified path.")]
    [DynamicResourceKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_CreateDirectory_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_CreateDirectory_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private void CreateDirectory(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [FromKernelServices] ChatContext chatContext,
        string path)
    {
        _logger.LogDebug("Creating directory at {Path}", path);

        ExpandFullPath(chatContext, ref path);
        displaySink.AppendFileReferences(new ChatPluginFileReference(path));

        Directory.CreateDirectory(path);
    }

    [KernelFunction("write_to_file")]
    [Description("Writes content to a text file at the specified path. Binary files are not supported.")]
    [DynamicResourceKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task WriteToFileAsync(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [FromKernelServices] ChatContext chatContext,
        string path,
        string? content,
        bool append = false)
    {
        _logger.LogDebug("Writing text file at {Path}, append: {Append}", path, append);

        ExpandFullPath(chatContext, ref path);
        displaySink.AppendFileReferences(new ChatPluginFileReference(path));

        await using var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        if (await EncodingDetector.DetectEncodingAsync(stream) is not { } encoding)
        {
            throw new HandledException(
                new UnauthorizedAccessException("Cannot write to a binary file."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_BinaryFile_Write_ErrorMessage),
                showDetails: false);
        }

        await using var writer = new StreamWriter(stream, encoding);
        await writer.WriteAsync(content);
        await writer.FlushAsync();
    }

    [KernelFunction("replace_file_content")]
    [Description("""
        Replaces content in a single text file at the specified path. Binary files are not supported.
        - Supports both regex and literal text replacement (toggle `isRegex`).
        - When using regex, replacements can use substitution patterns (e.g., $1, $2).
        """)]
    [DynamicResourceKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_ReplaceFileContent_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_ReplaceFileContent_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task<string> ReplaceFileContentAsync(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [FromKernelServices] ChatContext chatContext,
        string path,
        [Description("Text or regex patterns to search for within the file.")] IReadOnlyList<string> patterns,
        [Description("Replacement strings that match patterns.")] IReadOnlyList<string> replacements,
        [Description("Whether the patterns are regular expressions. Set to false for literal text replacement.")] bool isRegex = true,
        bool ignoreCase = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Replacing file content at {Path} with patterns: {Patterns}, replacements: {Replacements}, isRegex: {IsRegex}, ignoreCase: {IgnoreCase}",
            path,
            patterns,
            replacements,
            isRegex,
            ignoreCase);

        if (patterns.Count == 0)
        {
            throw new HandledFunctionInvokingException(
                HandledFunctionInvokingExceptionType.ArgumentError,
                nameof(patterns),
                new ArgumentException("At least one pattern must be provided.", nameof(patterns)));
        }

        if (replacements.Count != patterns.Count)
        {
            throw new HandledFunctionInvokingException(
                HandledFunctionInvokingExceptionType.ArgumentError,
                nameof(replacements),
                new ArgumentException("Replacements count must match patterns count.", nameof(replacements)));
        }

        ExpandFullPath(chatContext, ref path);
        displaySink.AppendFileReferences(new ChatPluginFileReference(path));

        var fileInfo = EnsureFileInfo(path);
        if (fileInfo.Length > 10 * 1024 * 1024)
        {
            throw new HandledException(
                new NotSupportedException("File size is larger than 10 MB, replace operation is not supported."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_ReplaceFileContent_FileTooLarge_ErrorMessage),
                showDetails: false);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken) is not { } encoding)
        {
            throw new HandledException(
                new InvalidOperationException("Cannot replace content in a binary file."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_ReplaceFileContent_BinaryFile_ErrorMessage),
                showDetails: false);
        }

        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, encoding);
        var fileContent = await reader.ReadToEndAsync(cancellationToken);

        var regexOptions = RegexOptions.Compiled | RegexOptions.Multiline;
        if (ignoreCase)
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }

        var replacedContent = fileContent;
        for (var i = 0; i < patterns.Count; i++)
        {
            var pattern = patterns[i];
            var replacement = i < replacements.Count ? replacements[i] : string.Empty;

            if (isRegex)
            {
                var regex = new Regex(pattern, regexOptions, RegexTimeout);
                replacedContent = regex.Replace(replacedContent, replacement);
            }
            else
            {
                var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                replacedContent = replacedContent.Replace(pattern, replacement, comparison);
            }
        }

        var difference = new TextDifference(path);
        TextDifferenceBuilder.BuildLineDiff(difference, fileContent, replacedContent);

        displaySink.AppendFileDifference(difference, fileContent);
        await difference.WaitForAcceptanceAsync(cancellationToken);

        // Apply all accepted changes
        if (!difference.Changes.Any(t => t.Accepted is true))
        {
            return "All changes were rejected by user.";
        }

        replacedContent = difference.Apply(fileContent);
        stream.SetLength(0);
        stream.Seek(0, SeekOrigin.Begin);
        await using var writer = new StreamWriter(stream, encoding);
        await writer.WriteAsync(replacedContent);
        await writer.FlushAsync(cancellationToken);

        return difference.ToModelSummary(fileContent, default);
    }

    private static void ExpandFullPath(ChatContext chatContext, ref string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new HandledFunctionInvokingException(
                HandledFunctionInvokingExceptionType.ArgumentError,
                nameof(path),
                new ArgumentException("Path cannot be null or empty.", nameof(path)));
        }

        var originalWorkingDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = chatContext.EnsureWorkingDirectory();
        try
        {
            path = Environment.ExpandEnvironmentVariables(path);
            path = Path.GetFullPath(path);
        }
        finally
        {
            Environment.CurrentDirectory = originalWorkingDirectory;
        }
    }

    /// <summary>
    /// Ensures the specified path is a valid directory and returns its DirectoryInfo.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="DirectoryNotFoundException"></exception>
    private static DirectoryInfo EnsureDirectoryInfo(string path)
    {
        var directoryInfo = new DirectoryInfo(path);
        if (directoryInfo.Exists) return directoryInfo;

        if (File.Exists(path))
        {
            throw new HandledException(
                new InvalidOperationException("The specified path is a file, not a directory."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_EnsureDirectoryInfo_PathIsFile_ErrorMessage),
                showDetails: false);
        }

        throw new HandledException(
            new DirectoryNotFoundException("The specified path is not a directory or a file."),
            new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_EnsureDirectoryInfo_PathNotExist_ErrorMessage),
            showDetails: false);
    }

    /// <summary>
    /// Ensures the specified path is a valid file and returns its FileInfo.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="FileNotFoundException"></exception>
    private static FileInfo EnsureFileInfo(string path)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Exists) return fileInfo;

        if (Directory.Exists(path))
        {
            throw new HandledException(
                new InvalidOperationException("The specified path is a directory, not a file."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_EnsureFileInfo_PathIsDirectory_ErrorMessage),
                showDetails: false);
        }

        throw new HandledException(
            new FileNotFoundException("The specified path is not a file or a directory."),
            new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_EnsureFileInfo_PathNotExist_ErrorMessage),
            showDetails: false);

    }

    private static FileSystemInfo EnsureFileSystemInfo(string path)
    {
        if (File.Exists(path))
        {
            return new FileInfo(path);
        }

        if (Directory.Exists(path))
        {
            return new DirectoryInfo(path);
        }

        throw new HandledException(
            new FileNotFoundException("The specified path does not exist as a file or directory."),
            new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_EnsureFileSystemInfo_PathNotExist_ErrorMessage),
            showDetails: false);
    }

    /// <summary>
    /// An enumerable that filters FileSystemInfo objects based on a regex pattern.
    /// </summary>
    private sealed class RegexFileSystemInfoEnumerable : FileSystemEnumerable<FileSystemInfo?>
    {
        public RegexFileSystemInfoEnumerable(string directory, Regex regex, bool recurseSubdirectories, bool ignoreCommonBuildFolders = false) : base(
            directory,
            (ref entry) =>
            {
                try
                {
                    return !regex.IsMatch(entry.FileName) ? null : entry.ToFileSystemInfo();
                }
                catch
                {
                    return null;
                }
            },
            CreateOptions(recurseSubdirectories, ignoreCommonBuildFolders))
        {
            if (ignoreCommonBuildFolders)
            {
                ShouldRecursePredicate = (ref entry) =>
                {
                    var name = entry.FileName;
                    return !name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("obj", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals(".vs", StringComparison.OrdinalIgnoreCase);
                };
            }
        }

        private static EnumerationOptions CreateOptions(bool recurseSubdirectories, bool ignoreCommonBuildFolders)
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                MatchType = MatchType.Simple,
                ReturnSpecialDirectories = false,
                MaxRecursionDepth = 32,
                RecurseSubdirectories = recurseSubdirectories
            };

            if (ignoreCommonBuildFolders)
            {
                options.AttributesToSkip = FileAttributes.Hidden | FileAttributes.System;
            }

            return options;
        }
    }

    private sealed class FileRenderer : IFriendlyFunctionCallContentRenderer
    {
        public ChatPluginDisplayBlock? Render(KernelArguments arguments)
        {
            if (!arguments.TryGetValue("path", out var pathObj) || pathObj is not string path) return null;

            // arguments.TryGetValue("filePattern", out var filePatternObj);
            return new ChatPluginFileReferencesDisplayBlock(new ChatPluginFileReference(path));
        }
    }
}