using System.ComponentModel;
using System.Text.RegularExpressions;
using Everywhere.Chat.Documents;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem;
using Everywhere.Common;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using ZLinq;

namespace Everywhere.Chat.Plugins.BuiltIn;

/// <summary>
/// Provides model-facing file operations while delegating resource semantics to file handlers.
/// </summary>
public sealed class FileSystemPlugin : BuiltInChatPlugin
{
#if WINDOWS
    private static StringComparison PathComparer => StringComparison.OrdinalIgnoreCase;
#else
    private static StringComparison PathComparer => StringComparison.Ordinal;
#endif

    private static TimeSpan RegexTimeout => TimeSpan.FromSeconds(3);

    public override IDynamicLocaleKey HeaderKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_Header);
    public override IDynamicLocaleKey DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_Description);
    public override LucideIconKind? Icon => LucideIconKind.FileBox;

    private readonly ILogger<FileSystemPlugin> _logger;
    private readonly FileHandlerContextFactory _contextFactory;

    public FileSystemPlugin(ILogger<FileSystemPlugin> logger, FileHandlerContextFactory contextFactory) : base("file_system")
    {
        _logger = logger;
        _contextFactory = contextFactory;

        _functionsSource.Edit(list =>
        {
            list.Add(new BuiltInChatFunction(SearchFilesAsync, ChatFunctionPermissions.FileRead));
            list.Add(new BuiltInChatFunction(GetFileInformationAsync, ChatFunctionPermissions.FileRead));
            list.Add(new BuiltInChatFunction(SearchFileContentAsync, ChatFunctionPermissions.FileRead));
            list.Add(new BuiltInChatFunction(ReadFileAsync, ChatFunctionPermissions.FileRead));
            list.Add(new BuiltInChatFunction(MoveFileAsync, ChatFunctionPermissions.FileAccess, onPermissionConsent: _ => true));
            list.Add(new BuiltInChatFunction(DeleteFilesAsync, ChatFunctionPermissions.FileAccess, onPermissionConsent: _ => true));
            list.Add(new BuiltInChatFunction(CreateDirectoryAsync, ChatFunctionPermissions.FileAccess, onPermissionConsent: _ => true));
            list.Add(new BuiltInChatFunction(WriteToFileAsync, ChatFunctionPermissions.FileAccess, onPermissionConsent: _ => true));
            list.Add(new BuiltInChatFunction(ReplaceFileContentAsync, ChatFunctionPermissions.FileAccess, onPermissionConsent: _ => true));
        });
    }

    // parts of algorithms for file searching are inspired by VS Code's implementation:
    // https://github.com/microsoft/vscode/tree/dc1de9b2cf2defca5e4fcfa120a7cf348e57b55b/extensions/copilot/src/extension/tools/node/findFilesTool.tsx
    [KernelFunction("search_files")]
    [Description("Search for files and directories in a path matching a regex. Common build and hidden folders are ignored.")]
    [DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_SearchFiles_Header, LocaleKey.BuiltInChatPlugin_FileSystem_SearchFiles_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task<PromptNode> SearchFilesAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        string path,
        [Description("Regex search pattern to match file and directory names")] string filePattern = ".*",
        int skip = 0,
        [Description("Maximum number of results to return. Max is 1000")] int maxCount = 100,
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

        var context = await _contextFactory.CreateAsync(path, chatContext.EnsureWorkingDirectory(), cancellationToken);
        userInterface.ActivityPreview = CreateFilePreview(context.Path, filePattern is ".*" ? null : new DirectLocaleKey(filePattern));
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(context.Path));

        var regex = CreateRegex(filePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var fileReferences = new List<ChatPluginFileReference>();
        var results = new List<string>();
        var totalResults = 0;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            await foreach (var item in context.Handler.EnumerateAsync(context, regex, true, true, cts.Token))
            {
                totalResults++;
                if (totalResults <= skip || results.Count >= maxCount) continue;

                results.Add(item);
                fileReferences.Add(new ChatPluginFileReference(item));
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

        userInterface.DisplaySink.AppendFileReferences(fileReferences);

        var output = new PromptTokenLimit(40000)
        {
            $"{totalResults} total {(totalResults == 1 ? "result" : "results")}{Environment.NewLine}"
        };
        for (var i = 0; i < results.Count; i++)
        {
            output.Add(new PromptText(results[i] + Environment.NewLine).WithPriority(1000 - i));
        }

        if (totalResults > skip + results.Count)
        {
            output.Add(
                new PromptText($"... {totalResults - skip - results.Count} more result(s) omitted due to maxCount{Environment.NewLine}")
                    .WithPriority(0));
        }

        return output;
    }

    [KernelFunction("get_file_info")]
    [Description("Get information about a file or directory at the specified path.")]
    [DynamicLocaleKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_GetFileInformation_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_GetFileInformation_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task<PromptNode> GetFileInformationAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        string path,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting file information for path: {Path}", path);

        var context = await _contextFactory.CreateAsync(path, chatContext.EnsureWorkingDirectory(), cancellationToken);
        userInterface.ActivityPreview = CreateFilePreview(context.Path);
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(context.Path));

        var record = await context.Handler.GetFileInformationAsync(context, cancellationToken);
        return $"{FileRecord.Header}{Environment.NewLine}{record}";
    }

    [KernelFunction("search_file_content")]
    [Description("Search text in one file or all matching files below a directory. Supports regex and literal patterns.")]
    [DynamicLocaleKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_SearchFileContent_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_SearchFileContent_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task<PromptNode> SearchFileContentAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        [Description("File or directory path to search")] string path,
        [Description("Text or regex pattern to search for within the file")] string pattern,
        [Description("Whether the pattern is a regular expression")] bool isRegex = true,
        bool ignoreCase = true,
        [Description("Regex pattern to include files to search")] string filePattern = ".*",
        [Description("Maximum number of matching lines to return. Max is 200")] int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Searching file content in path: {Path} with pattern: {SearchPattern}, isRegex: {IsRegex}, ignoreCase: {IgnoreCase}, filePattern: {FilePattern}",
            path,
            pattern,
            isRegex,
            ignoreCase,
            filePattern);

        var options = RegexOptions.Compiled | RegexOptions.Multiline;
        if (ignoreCase) options |= RegexOptions.IgnoreCase;
        var searchRegex = CreateRegex(isRegex ? pattern : Regex.Escape(pattern), options);
        var fileRegex = CreateRegex(filePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var root = await _contextFactory.CreateAsync(path, chatContext.EnsureWorkingDirectory(), cancellationToken);
        userInterface.ActivityPreview = CreateFilePreview(root.Path, new DirectLocaleKey(pattern));
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(root.Path));

        maxResults = Math.Clamp(maxResults, 1, 200);
        var matches = new List<FileContentMatch>();
        var limitHit = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            await foreach (var item in root.Handler.EnumerateAsync(root, fileRegex, true, true, cts.Token))
            {
                if (matches.Count >= maxResults * 5) break;
                var context = await _contextFactory.CreateAsync(item, root.WorkingDirectory, cts.Token);
                try
                {
                    var result = await context.Handler.SearchContentAsync(context, searchRegex, cts.Token);
                    matches.AddRange(result.Matches.Take(maxResults * 5 - matches.Count));
                    limitHit |= result.LimitHit;
                }
                catch (HandledException ex) when (ex.InnerException is NotSupportedException)
                {
                    // Binary files and directories are deliberately skipped during a recursive search.
                }
                catch (HandledException ex) when (ex.InnerException is InvalidOperationException && context.FileSystemInfo is DirectoryInfo)
                {
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (matches.Count == 0)
            {
                return "Search timed out after 20 seconds. Found 0 files with matches so far. Try a more specific search pattern or path.";
            }

            limitHit = true;
        }

        if (matches.Count == 0)
        {
            return $"No matching lines found for {(isRegex ? "regex" : "literal text")} '{pattern}'.";
        }

        return BuildSearchOutput(userInterface.DisplaySink, matches, pattern, maxResults, limitHit);
    }

    [KernelFunction("read_file")]
    [Description(
        """
        Read a local path, file:// URI, or a skill:// resource in bounded chunks.
        Text files use 1-based logical line offsets; PDFs use text extraction and global logical line offsets with page metadata; binary files use 1-based byte offsets and return hexadecimal data.
        docx, xlsx, pptx are not supported. Use `officecli` instead.
        """)]
    [DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_Header, LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task<object> ReadFileAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        [Description(
            """
            Path or URI of the file.
            A relative local path resolves against the current working directory; absolute paths and file:// URIs are supported. 
            A Skill resource must use a complete source-qualified ID in the form skill://{source}.{skill}/{relative-path}, for example skill://builtin.officecli/SKILL.md. Short IDs such as skill://officecli are invalid.
            Other URI schemes are unsupported.
            """)]
        string path,
        [Description("1-based line or byte offset. Use `nextOffset` from the previous result to continue.")]
        int offset = 1,
        [Description("Maximum number of logical lines or bytes to return.")] int limit = 2000,
        [Description("Treat a local file as an attachment. Keep this as false for most use cases.")] bool attachment = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Reading file at path: {Path}, offset: {Offset}, limit: {Limit}, attachment: {Attachment}",
            path,
            offset,
            limit,
            attachment);

        var context = await _contextFactory.CreateAsync(path, chatContext.EnsureWorkingDirectory(), cancellationToken);
        userInterface.ActivityPreview = CreateFilePreview(context.Path);
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(context.Path));
        if (attachment)
        {
            if (context.FileSystemInfo is not FileInfo { Exists: true } file)
            {
                throw ToHandledException(
                    new NotSupportedException("attachment=true is supported only for local files."),
                    LocaleKey.BuiltInChatPlugin_FileSystem_LocalPathOnly_ErrorMessage);
            }

            return file.Length switch
            {
                0 => $"(The file `{context.Path}` exists, but is empty)",
                > 10L * 1024 * 1024 => throw new HandledException(
                    new NotSupportedException("Attachment file size is larger than 10 MB."),
                    new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_FileTooLarge_ErrorMessage),
                    showDetails: false),
                _ => await FileAttachment.CreateAsync(context.Path, cancellationToken: cancellationToken)
            };
        }

        var result = await context.Handler.ReadAsync(context, offset, limit, cancellationToken);
        return BuildReadOutput(context.Path, result);
    }

    [KernelFunction("move_file")]
    [Description("Moves or renames a local file or directory.")]
    [DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_MoveFile_Header, LocaleKey.BuiltInChatPlugin_FileSystem_MoveFile_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task MoveFileAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        string source,
        string destination,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Moving file from {Source} to {Destination}", source, destination);

        source = ExpandLocalPath(chatContext, source);
        destination = ExpandLocalPath(chatContext, destination);
        userInterface.ActivityPreview = new ChatPluginFileTransferActivityPreview(
            new ChatPluginFileReference(source),
            new ChatPluginFileReference(destination));
        userInterface.DisplaySink.AppendFileReferences(
            new ChatPluginFileReference(source),
            new ChatPluginFileReference(destination));

        var isFile = File.Exists(source);
        if (!isFile && !Directory.Exists(source))
        {
            throw ToHandledException(
                new FileNotFoundException("Source does not exist.", source),
                LocaleKey.HandledSystemException_FileNotFound);
        }

        var destinationDirectory = Path.GetDirectoryName(destination) ??
            throw ToHandledException(
                new DirectoryNotFoundException("Destination directory is invalid."),
                LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPath_ErrorMessage);

        await RequestFileOperationConsentAsync(
            userInterface,
            chatContext,
            new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_MoveFile_MoveConsent_Header),
            null,
            [source, destination],
            cancellationToken);

        Directory.CreateDirectory(destinationDirectory);
        if (isFile) File.Move(source, destination, overwrite: false);
        else Directory.Move(source, destination);
    }

    [KernelFunction("delete_files")]
    [Description("Delete local files and directories matching a regex.")]
    [DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_Header, LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task<string> DeleteFilesAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        string path,
        string filePattern = ".*",
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting file at {Path}", path);

        path = ExpandLocalPath(chatContext, path);
        userInterface.ActivityPreview = CreateFilePreview(
            path,
            filePattern is ".*" ? null : new DirectLocaleKey(filePattern));
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(path));
        if (Path.GetDirectoryName(path) is null)
        {
            throw ToHandledException(
                new UnauthorizedAccessException("Cannot delete a root directory."),
                LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_RootDirectory_Deletion_ErrorMessage);
        }

        var root = EnsureFileSystemInfo(path);
        if (root.Attributes.HasFlag(FileAttributes.System))
        {
            throw ToHandledException(
                new UnauthorizedAccessException("Cannot delete a system file or directory."),
                LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_SystemFile_Deletion_ErrorMessage);
        }

        var regex = CreateRegex(filePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var targets = root is FileInfo ? [root] : EnumerateLocalEntries((DirectoryInfo)root, regex, cancellationToken).ToList();
        if (targets.Count == 0) return "No files or directories to delete.";

        var paths = targets.AsValueEnumerable().Select(info => info.FullName).Order().ToList();
        await RequestFileOperationConsentAsync(
            userInterface,
            chatContext,
            new FormattedDynamicLocaleKey(
                LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_DeletionConsent_Header,
                new DirectLocaleKey(targets.Count)),
            null,
            paths,
            cancellationToken);

        var success = 0;
        var errors = 0;
        foreach (var target in targets.OrderByDescending(info => info.FullName.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (target.Exists)
                {
                    if (target is DirectoryInfo directory) directory.Delete(true);
                    else target.Delete();
                }

                success++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete {Path}", target.FullName);
                errors++;
            }
        }

        return errors == 0 ?
            $"{success} files/directories were deleted successfully." :
            $"{success} files/directories were deleted successfully, {errors} errors occurred.";
    }

    [KernelFunction("create_directory")]
    [Description("Creates a new local directory.")]
    [DynamicLocaleKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_CreateDirectory_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_CreateDirectory_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task CreateDirectoryAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        string path,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating directory at {Path}", path);

        path = ExpandLocalPath(chatContext, path);
        userInterface.ActivityPreview = CreateFilePreview(path);
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(path));
        await RequestFileOperationConsentAsync(
            userInterface,
            chatContext,
            new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_CreateDirectory_CreateConsent_Header),
            null,
            [path],
            cancellationToken);
        Directory.CreateDirectory(path);
    }

    [KernelFunction("write_to_file")]
    [Description("Writes content to a text file. Unsupported handlers reject the operation.")]
    [DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_Header, LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task WriteToFileAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        string path,
        string? content,
        bool append = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Writing text file at {Path}, append: {Append}", path, append);

        var context = await _contextFactory.CreateAsync(path, chatContext.EnsureWorkingDirectory(), cancellationToken);
        userInterface.ActivityPreview = CreateFilePreview(context.Path);
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(context.Path));
        var exists = context.FileSystemInfo is FileInfo { Exists: true };
        await RequestFileOperationConsentAsync(
            userInterface,
            chatContext,
            new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_WriteConsent_Header),
            new DynamicLocaleKey(GetWriteConsentDescriptionKey(append, exists)),
            [context.Path],
            cancellationToken);
        await context.Handler.WriteAsync(context, content, append, cancellationToken);
    }

    [KernelFunction("replace_file_content")]
    [Description("Replaces text in one file after presenting a diff for review.")]
    [DynamicLocaleKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_ReplaceFileContent_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_ReplaceFileContent_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task<string> ReplaceFileContentAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        string path,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> replacements,
        bool isRegex = true,
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
            throw ToHandledException(
                new ArgumentException("At least one pattern must be provided.", nameof(patterns)),
                LocaleKey.BuiltInChatPlugin_FileSystem_InvalidReplacement_ErrorMessage);
        }

        if (patterns.Count != replacements.Count)
        {
            throw ToHandledException(
                new ArgumentException("Replacements count must match patterns count.", nameof(replacements)),
                LocaleKey.BuiltInChatPlugin_FileSystem_InvalidReplacement_ErrorMessage);
        }

        var context = await _contextFactory.CreateAsync(path, chatContext.EnsureWorkingDirectory(), cancellationToken);
        userInterface.ActivityPreview = CreateFilePreview(context.Path);
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(context.Path));
        return await context.Handler.ReplaceContentAsync(
            context,
            patterns,
            replacements,
            isRegex,
            ignoreCase,
            async (original, proposed, token) =>
            {
                var difference = new TextDifference(context.Path);
                TextDifferenceBuilder.BuildLineDiff(difference, original, proposed);
                userInterface.DisplaySink.AppendFileDifference(difference, original);
                await difference.WaitForAcceptanceAsync(token);
                return difference.Changes.AsValueEnumerable().Any(change => change.Accepted is true) ?
                    new FileReviewResult(difference.Apply(original), difference.ToModelSummary(original, default)) :
                    new FileReviewResult(null, "All changes were rejected by user.");
            },
            cancellationToken);
    }

    private static PromptNode BuildReadOutput(string path, FileReadResult result)
    {
        if (result.Items.Count == 0 && result.Total == 0)
        {
            return $"(The file `{path}` exists, but is empty)";
        }

        if (result.Items.Count == 0)
        {
            return $"(No content at {result.Unit} offset {result.Offset} in `{path}`)";
        }

        var unitName = char.ToUpperInvariant(result.Unit[0]) + result.Unit[1..] + "s";
        var details = result.Total is { } total ? $" ({total} {result.Unit}s total)" : string.Empty;
        var metadata = string.Join(
            string.Empty,
            result.Metadata
                .Where(static item => item.Value is not null)
                .Select(static item => $", {item.Key}={item.Value}"));
        var output = new PromptTokenLimit(40000)
        {
            $"File: `{path}`. {unitName} starting at {result.Offset}{details}{metadata}:{Environment.NewLine}"
        };

        var position = result.Offset;
        int? currentPage = null;
        for (var i = 0; i < result.Items.Count; i++)
        {
            var item = result.Items[i];
            var pagePrefix = item.PageNumber != currentPage && item.PageNumber is { } page ? $"[Page {page}]{Environment.NewLine}" : string.Empty;
            output.Add(
                new PromptTextChunk($"{pagePrefix}{position}: {item.Content}{Environment.NewLine}")
                    .BreakOnWhitespace()
                    .WithPriority(1000 - i));
            currentPage = item.PageNumber ?? currentPage;
            position += item.UnitCount;
        }

        if (result.HasMore)
        {
            // This note is pruned before content lines. If pruning occurs, their numeric prefixes
            // still let the model continue after the last line it actually received.
            output.Add(
                new PromptText($"{Environment.NewLine}[More content is available. Continue with offset={result.NextOffset}.]{Environment.NewLine}")
                    .WithPriority(int.MinValue));
        }

        return output;
    }

    private static PromptNode BuildSearchOutput(
        IChatPluginDisplaySink displaySink,
        IReadOnlyList<FileContentMatch> matches,
        string pattern,
        int maxResults,
        bool limitHit)
    {
        var files = matches
            .AsValueEnumerable()
            .GroupBy(static match => match.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SearchFileGroup(
                group.Key,
                group
                    .AsValueEnumerable()
                    .GroupBy(static match => (match.PageNumber, match.Line))
                    .OrderBy(static line => line.Key.PageNumber)
                    .ThenBy(static line => line.Key.Line)
                    .Select(static line => new SearchLineGroup([.. line]))
                    .ToList()))
            .ToList();

        AllocateSearchLines(files, maxResults);
        var shownFiles = files.AsValueEnumerable().Where(static file => file.ShownCount > 0).ToList();
        var totalLines = files.AsValueEnumerable().Sum(static file => file.Lines.Count);
        var shownLines = shownFiles.AsValueEnumerable().Sum(static file => file.ShownCount);
        var shownOccurrences = shownFiles.AsValueEnumerable().Sum(static file =>
            file.Lines.AsValueEnumerable().Take(file.ShownCount).Sum(static line => line.Matches.Count));
        var qualifier = shownLines < totalLines ?
            $" (showing {shownOccurrences} {Pluralize(shownOccurrences, "occurrence", "occurrences")} on " +
            $"{shownLines} {Pluralize(shownLines, "line", "lines")} in " +
            $"{shownFiles.Count} {Pluralize(shownFiles.Count, "file", "files")})" :
            string.Empty;
        if (limitHit) qualifier += " (search limit reached)";

        var output = new PromptTokenLimit(40000)
        {
            $"Found {matches.Count} {Pluralize(matches.Count, "occurrence", "occurrences")} on " +
            $"{totalLines} matching {Pluralize(totalLines, "line", "lines")} in " +
            $"{files.Count} {Pluralize(files.Count, "file", "files")} for \"{pattern}\"{qualifier}{Environment.NewLine}"
        };

        for (var fileIndex = 0; fileIndex < shownFiles.Count; fileIndex++)
        {
            var file = shownFiles[fileIndex];
            var lines = new List<string>(file.ShownCount + 2) { string.Empty, file.Path };
            var locations = new HashSet<ChatPluginFileReferenceLocation>();
            foreach (var line in file.Lines.AsValueEnumerable().Take(file.ShownCount))
            {
                var first = line.Matches[0];
                var page = first.PageNumber is { } pageNumber ? $" [page {pageNumber}]" : string.Empty;
                lines.Add($"{first.Line}{page}:{BoundMatchPreview(first)}");
                foreach (var match in line.Matches.AsValueEnumerable())
                {
                    locations.Add(new ChatPluginFileReferenceLocation(match.Line, match.Column));
                }
            }

            if (file.ShownCount < file.Lines.Count)
            {
                lines.Add($"... ({file.Lines.Count - file.ShownCount} more matching line(s) in this file)");
            }

            displaySink.AppendFileReferences(new ChatPluginFileReference(file.Path, locations: locations));

            // Matching lines stay together so the renderer removes complete file blocks.
            output.Add(new PromptText(string.Join(Environment.NewLine, lines)).WithPriority(1000 - fileIndex));
        }

        return output;
    }

    private static void AllocateSearchLines(IReadOnlyList<SearchFileGroup> files, int maxResults)
    {
        var keptFiles = files.AsValueEnumerable().Take(maxResults).ToList();
        foreach (var file in keptFiles.AsValueEnumerable()) file.ShownCount = 1;

        var remaining = maxResults - keptFiles.Count;
        var capacity = keptFiles.AsValueEnumerable().Sum(static file => file.Lines.Count - 1);
        if (remaining <= 0 || capacity <= 0) return;

        var allocations = keptFiles
            .AsValueEnumerable()
            .Select(file =>
            {
                // ReSharper disable once AccessToModifiedClosure
                var exact = (double)(file.Lines.Count - 1) / capacity * remaining;
                var added = Math.Min(file.Lines.Count - 1, (int)Math.Floor(exact));
                return new SearchLineAllocation(file, added, exact - Math.Floor(exact));
            })
            .ToList();
        foreach (var allocation in allocations.AsValueEnumerable()) allocation.File.ShownCount += allocation.Added;

        remaining -= allocations.AsValueEnumerable().Sum(static allocation => allocation.Added);
        foreach (var allocation in allocations.AsValueEnumerable().OrderByDescending(static allocation => allocation.Remainder))
        {
            if (remaining == 0) break;
            if (allocation.File.ShownCount >= allocation.File.Lines.Count) continue;
            allocation.File.ShownCount++;
            remaining--;
        }
    }

    private static string BoundMatchPreview(FileContentMatch match)
    {
        const int maxLineLength = 600;
        const int contextBeforeLength = 150;
        const int contextAfterLength = 105;
        const int maxMatchLength = 300;
        const int headLength = (maxMatchLength + 1) / 2;
        const int tailLength = maxMatchLength - headLength;

        var preview = match.Preview.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').TrimEnd();
        if (preview.Length <= maxLineLength) return preview;

        var start = Math.Clamp(match.Column - 1, 0, preview.Length);
        var end = Math.Clamp(start + match.Length, start, preview.Length);
        var matchText = preview[start..end];
        if (matchText.Length > maxMatchLength)
        {
            matchText = $"{matchText[..headLength]}[... {matchText.Length - maxMatchLength} characters elided ...]{matchText[^tailLength..]}";
        }

        var before = preview[Math.Max(0, start - contextBeforeLength)..start];
        var after = preview[end..Math.Min(preview.Length, end + contextAfterLength)];
        return $"{before}{matchText}{after} [match at col {match.Column} · line truncated, {preview.Length:N0} chars]";
    }

    private static string Pluralize(int count, string singular, string plural) => count == 1 ? singular : plural;

    private static Regex CreateRegex(string pattern, RegexOptions options)
    {
        try
        {
            return new Regex(pattern, options, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw ToHandledException(ex, LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPattern_ErrorMessage);
        }
    }

    private static string ExpandLocalPath(ChatContext chatContext, string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            throw ToHandledException(
                new NotSupportedException("This operation supports local paths only."),
                LocaleKey.BuiltInChatPlugin_FileSystem_LocalPathOnly_ErrorMessage);
        }

        try
        {
            return ExpandFullPath(chatContext.EnsureWorkingDirectory(), uri?.IsFile == true ? uri.LocalPath : path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw ToHandledException(ex, LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPath_ErrorMessage);
        }
    }

    internal static string ExpandFullPath(string workingDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw ToHandledException(
                new ArgumentException("Path cannot be null or empty.", nameof(path)),
                LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPath_ErrorMessage);
        }

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw ToHandledException(
                new ArgumentException("Working directory cannot be null or empty.", nameof(workingDirectory)),
                LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPath_ErrorMessage);
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path), workingDirectory);
    }

    internal static bool IsPathInsideDirectory(string path, string directory)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return fullPath.Equals(fullDirectory, PathComparer) || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, PathComparer);
    }

    internal static string GetWriteConsentDescriptionKey(bool append, bool fileExists) =>
        fileExists ?
            append ?
                LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_AppendConsent_Description :
                LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_OverwriteConsent_Description :
            LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_CreateConsent_Description;

    /// <summary>
    /// Creates the compact request preview used by file operations. The detailed file reference is
    /// still appended to the display sink separately, so this helper never duplicates durable output
    /// or exposes operation results in the running activity row.
    /// </summary>
    private static ChatPluginFileReferencesActivityPreview CreateFilePreview(string path, IDynamicLocaleKey? prefixKey = null) =>
        new([new ChatPluginFileReference(path)], prefixKey);

    private static async Task RequestFileOperationConsentAsync(
        IChatPluginUserInterface userInterface,
        ChatContext chatContext,
        IDynamicLocaleKey headerKey,
        IDynamicLocaleKey? descriptionKey,
        List<string> paths,
        CancellationToken cancellationToken)
    {
        var workingDirectory = chatContext.EnsureWorkingDirectory();
        if (paths.Count > 0 && paths.All(path => Path.IsPathFullyQualified(path) && IsPathInsideDirectory(path, workingDirectory))) return;
        var container = new ChatPluginContainerDisplayBlock();
        if (descriptionKey is not null)
        {
            container.Add(new ChatPluginDynamicLocaleKeyDisplayBlock(descriptionKey));
        }

        container.Add(
            new ChatPluginFileReferencesDisplayBlock(
                paths.AsValueEnumerable().Select(path => new ChatPluginFileReference(path)).ToList())
            {
                TotalReferenceCount = paths.Count
            });
        var consent = await userInterface.RequestConsentAsync(
            BuildConsentId(paths),
            headerKey,
            container,
            RequestConsentRememberMasks.AllowOnce | RequestConsentRememberMasks.AllowSession,
            cancellationToken: cancellationToken);
        if (!consent)
        {
            throw ToHandledException(
                new UnauthorizedAccessException(consent.FormatReason("User denied consent for this operation.")),
                LocaleKey.BuiltInChatPlugin_FileSystem_ConsentDenied_ErrorMessage);
        }
    }

    private static string BuildConsentId(IReadOnlyList<string> paths) =>
        "|" + string.Join('|', paths.Order().Select(Path.TrimEndingDirectorySeparator));

    private static FileSystemInfo EnsureFileSystemInfo(string path)
    {
        if (File.Exists(path)) return new FileInfo(path);
        if (Directory.Exists(path)) return new DirectoryInfo(path);
        throw ToHandledException(
            new FileNotFoundException("The specified path does not exist.", path),
            LocaleKey.BuiltInChatPlugin_FileSystem_EnsureFileSystemInfo_PathNotExist_ErrorMessage);
    }

    private static IEnumerable<FileSystemInfo> EnumerateLocalEntries(DirectoryInfo root, Regex regex, CancellationToken cancellationToken)
    {
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((root, 0));

        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemInfo[] entries;
            try
            {
                entries = current.Directory.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (regex.IsMatch(entry.Name)) yield return entry;
                if (entry is DirectoryInfo directory && current.Depth < 32) pending.Push((directory, current.Depth + 1));
            }
        }
    }

    private static HandledException ToHandledException(Exception exception, string friendlyMessageKey) =>
        new(exception, new DynamicLocaleKey(friendlyMessageKey), showDetails: false);

    /// <summary>
    /// Holds matching logical lines and their output allocation for one file.
    /// </summary>
    private sealed class SearchFileGroup(string path, List<SearchLineGroup> lines)
    {
        public string Path { get; } = path;
        public List<SearchLineGroup> Lines { get; } = lines;
        public int ShownCount { get; set; }
    }

    /// <summary>
    /// Groups all occurrences that share one model-facing logical line.
    /// </summary>
    private sealed class SearchLineGroup(List<FileContentMatch> matches)
    {
        public List<FileContentMatch> Matches { get; } = matches;
    }

    private readonly record struct SearchLineAllocation(SearchFileGroup File, int Added, double Remainder);

    /// <summary>
    /// Renders the path argument as a friendly file reference in the chat UI.
    /// </summary>
    private sealed class FileRenderer : IFriendlyFunctionCallContentRenderer
    {
        public ChatPluginDisplayBlock? Render(KernelArguments arguments) =>
            arguments.TryGetValue("path", out var value) && value is string path ?
                new ChatPluginFileReferencesDisplayBlock(new ChatPluginFileReference(path)) :
                null;
    }
}
