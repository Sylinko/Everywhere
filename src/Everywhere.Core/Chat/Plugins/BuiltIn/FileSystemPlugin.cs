using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Prompting.Documents;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat.Plugins.BuiltIn;

/// <summary>
/// Provides model-facing file operations while delegating resource semantics to file handlers.
/// </summary>
public sealed class FileSystemPlugin : BuiltInChatPlugin
{
    private static TimeSpan RegexTimeout => TimeSpan.FromSeconds(3);

    public override IDynamicLocaleKey HeaderKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_Header);
    public override IDynamicLocaleKey DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_Description);
    public override LucideIconKind? Icon => LucideIconKind.FileBox;
    public override bool IsDefaultEnabled => true;
    public override IReadOnlyList<SettingsItem> SettingsItems => _fileSystemSettings.SettingsItems;

    private readonly FileSystemSettings _fileSystemSettings;
    private readonly FileHandlerContextFactory _contextFactory;
    private readonly ILogger<FileSystemPlugin> _logger;

    public FileSystemPlugin(Settings settings, FileHandlerContextFactory contextFactory, ILogger<FileSystemPlugin> logger) : base("file_system")
    {
        _fileSystemSettings = settings.Plugin.FileSystem;
        _contextFactory = contextFactory;
        _logger = logger;

        _functionsSource.Edit(list =>
        {
            list.Add(new BuiltInChatFunction(SearchFilesAsync, ChatFunctionPermissions.FileRead));
            list.Add(new BuiltInChatFunction(GetFileInformationAsync, ChatFunctionPermissions.FileRead));
            list.Add(new BuiltInChatFunction(SearchFileContentAsync, ChatFunctionPermissions.FileRead));
            list.Add(new BuiltInChatFunction(ReadFileAsync, ChatFunctionPermissions.FileRead));
            list.Add(new BuiltInChatFunction(TransferPathAsync, ChatFunctionPermissions.FileAccess, onPermissionConsent: _ => true));
            list.Add(new BuiltInChatFunction(DeletePathsAsync, ChatFunctionPermissions.FileAccess, onPermissionConsent: _ => true));
            list.Add(new BuiltInChatFunction(CreateDirectoryAsync, ChatFunctionPermissions.FileAccess, onPermissionConsent: _ => true));
            list.Add(new BuiltInChatFunction(ApplyPatchAsync, ChatFunctionPermissions.FileAccess, onPermissionConsent: _ => true));
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
        var internalMaxResults = maxResults * 5;
        var matches = new List<FileContentMatch>();
        var limitHit = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            await foreach (var item in root.Handler.EnumerateAsync(root, fileRegex, true, true, cts.Token))
            {
                if (matches.Count >= internalMaxResults)
                {
                    limitHit = true;
                    break;
                }

                var context = await _contextFactory.CreateAsync(item, root.WorkingDirectory, cts.Token);
                try
                {
                    var result = await context.Handler.SearchContentAsync(context, searchRegex, cts.Token);
                    var remaining = internalMaxResults - matches.Count;
                    limitHit |= result.LimitHit || result.Matches.Count > remaining;
                    matches.AddRange(result.Matches.Take(remaining));
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
        [Description("Treat a local file as an attachment. Keep this as false for most use cases.")]
        bool attachment = false,
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
                throw new HandledException(
                    new NotSupportedException(
                        "The 'attachment' option was requested, but the path is not an existing local file. Only existing local files can be attached."),
                    LocaleKey.BuiltInChatPlugin_FileSystem_LocalPathOnly_ErrorMessage);
            }

            return file.Length switch
            {
                0 => $"(The file `{context.Path}` exists, but is empty)",
                > 10L * 1024 * 1024 => throw new HandledException(
                    new NotSupportedException(
                        "The requested attachment is larger than 10 MB, so it cannot be attached."),
                    new FormattedDynamicLocaleKey(
                        LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_FileTooLarge_ErrorMessage,
                        10),
                    showDetails: false),
                _ => await FileAttachment.CreateAsync(context.Path, cancellationToken: cancellationToken)
            };
        }

        var result = await context.Handler.ReadAsync(context, offset, limit, cancellationToken);
        return BuildReadOutput(context.Path, result);
    }

    [KernelFunction("transfer_path")]
    [Description(
        "Copies or moves a local file or directory to a new path. Moving to a new name renames it. " +
        "The destination must not already exist.")]
    [DynamicLocaleKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_TransferPath_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_TransferPath_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileTransferRenderer))]
    private async Task TransferPathAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        [Description("The existing local file or directory to copy or move.")] string source,
        [Description("The destination path, including the final file or directory name.")] string destination,
        [Description("The operation to perform: copy or move.")] FileTransferOperation operation,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "{Operation} file from {Source} to {Destination}",
            operation,
            source,
            destination);

        source = ExpandLocalPath(chatContext, source);
        destination = ExpandLocalPath(chatContext, destination);
        if (operation is not FileTransferOperation.Copy and not FileTransferOperation.Move)
        {
            throw new HandledException(
                new ArgumentOutOfRangeException(
                    nameof(operation),
                    operation,
                    "The transfer_path operation must be either Copy or Move."),
                LocaleKey.HandledSystemException_ArgumentOutOfRange);
        }

        userInterface.ActivityPreview = new ChatPluginFileTransferActivityPreview(
            new ChatPluginFileReference(source),
            new DynamicLocaleKey(
                operation is FileTransferOperation.Copy ?
                    LocaleKey.BuiltInChatPlugin_FileSystem_TransferPath_CopyTo :
                    LocaleKey.BuiltInChatPlugin_FileSystem_TransferPath_MoveTo),
            new ChatPluginFileReference(destination));
        userInterface.DisplaySink.AppendFileReferences(
            new ChatPluginFileReference(source),
            new ChatPluginFileReference(destination));

        var operationName = operation is FileTransferOperation.Copy ? "copy" : "move";
        var isFile = File.Exists(source);
        if (!isFile && !Directory.Exists(source))
        {
            throw new HandledException(
                new FileNotFoundException($"The source path does not exist, so cannot {operationName}: '{source}'.", source),
                LocaleKey.HandledSystemException_FileNotFound);
        }

        if (string.Equals(
                Path.TrimEndingDirectorySeparator(source),
                Path.TrimEndingDirectorySeparator(destination),
                PathContainment.SystemPathComparison))
        {
            throw new HandledException(
                new IOException($"The source and destination resolve to the same path, so the {operationName} operation cannot be performed."),
                LocaleKey.HandledSystemException_IOException);
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new HandledException(
                new IOException($"The destination path already exists, so the {operationName} operation was not performed: '{destination}'."),
                LocaleKey.HandledSystemException_IOException);
        }

        if (!isFile && PathContainment.IsInsideDirectory(destination, source))
        {
            throw new HandledException(
                new IOException($"The destination directory is inside the source directory, so the {operationName} operation cannot be performed."),
                LocaleKey.HandledSystemException_IOException);
        }

        var destinationDirectory = Path.GetDirectoryName(destination) ??
            throw new HandledException(
                new DirectoryNotFoundException($"The destination path does not contain a valid parent directory: '{destination}'."),
                LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPath_ErrorMessage);

        if (operation is FileTransferOperation.Copy && isFile && File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new HandledException(
                new NotSupportedException(
                    $"The file copy was not performed because '{source}' is a symbolic link. Copying linked files is not supported."),
                LocaleKey.HandledSystemException_NotSupported);
        }

        if (operation is FileTransferOperation.Copy && !isFile)
        {
            EnsureDirectoryCanBeCopied(new DirectoryInfo(source), cancellationToken);
        }

        await RequestFileOperationConsentAsync(
            userInterface,
            chatContext,
            new FormattedDynamicLocaleKey(
                operation is FileTransferOperation.Copy ?
                    LocaleKey.BuiltInChatPlugin_FileSystem_TransferPath_CopyConsent_Header :
                    LocaleKey.BuiltInChatPlugin_FileSystem_TransferPath_MoveConsent_Header,
                new DirectLocaleKey(Path.GetFileName(Path.TrimEndingDirectorySeparator(source))),
                new DirectLocaleKey(Path.GetFileName(Path.TrimEndingDirectorySeparator(destination)))),
            [source, destination],
            () => CreateFileTransferContent(source, destination, operation),
            cancellationToken);

        Directory.CreateDirectory(destinationDirectory);
        switch (operation)
        {
            case FileTransferOperation.Copy when isFile:
                File.Copy(source, destination, overwrite: false);
                break;
            case FileTransferOperation.Copy:
                CopyDirectory(new DirectoryInfo(source), destination, cancellationToken);
                break;
            case FileTransferOperation.Move when isFile:
                File.Move(source, destination, overwrite: false);
                break;
            case FileTransferOperation.Move:
                Directory.Move(source, destination);
                break;
        }
    }

    [KernelFunction("delete_paths")]
    [Description("Delete explicitly listed local files or directories. Non-empty directories require recursive=true.")]
    [DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_DeletePaths_Header, LocaleKey.BuiltInChatPlugin_FileSystem_DeletePaths_Description)]
    private async Task<string> DeletePathsAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        [Description("Explicit local file or directory paths to delete.")] IReadOnlyList<string> paths,
        [Description("Whether non-empty directories may be deleted recursively.")] bool recursive = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting paths: {Paths}, recursive: {Recursive}", paths, recursive);
        if (paths.Count == 0)
        {
            throw new HandledException(
                new ArgumentException("At least one path must be provided.", nameof(paths)),
                LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPath_ErrorMessage);
        }

        var workingDirectory = chatContext.EnsureWorkingDirectory();
        var targets = paths
            .AsValueEnumerable()
            .Select(path => ExpandLocalPath(chatContext, path))
            .Distinct(PathContainment.SystemPathComparer)
            .Select(path => PrepareDeleteTarget(path, workingDirectory, recursive, cancellationToken))
            .ToArray();
        if (targets.Length == 0) return "No files or directories to delete.";

        var targetPaths = targets
            .AsValueEnumerable()
            .Select(static info => info.FullName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        userInterface.ActivityPreview = new ChatPluginFileReferencesActivityPreview(
            targetPaths.AsValueEnumerable().Select(static path => new ChatPluginFileReference(path)).ToArray());
        userInterface.DisplaySink.AppendFileReferences(
            targetPaths.AsValueEnumerable().Select(static path => new ChatPluginFileReference(path)).ToArray());
        var requiresExplicitApproval = recursive && targets.AsValueEnumerable().Any(static target => target is DirectoryInfo);
        await RequestFileOperationConsentAsync(
            userInterface,
            chatContext,
            new FormattedDynamicLocaleKey(
                LocaleKey.BuiltInChatPlugin_FileSystem_DeletePaths_DeletionConsent_Header,
                targets.Length),
            targetPaths,
            null,
            cancellationToken,
            forceConsent: requiresExplicitApproval);

        var success = 0;
        var errors = 0;
        foreach (var target in targets.OrderByDescending(static info => info.FullName.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                target.Refresh();
                if (target.Exists)
                {
                    if (target is DirectoryInfo directory) directory.Delete(recursive);
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

    private static FileSystemInfo PrepareDeleteTarget(
        string path,
        string workingDirectory,
        bool recursive,
        CancellationToken cancellationToken)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? string.Empty);
        if (string.Equals(normalizedPath, root, PathContainment.SystemPathComparison))
        {
            throw new HandledException(
                new UnauthorizedAccessException(
                    "The requested path is a filesystem root, and root directories cannot be deleted."),
                LocaleKey.BuiltInChatPlugin_FileSystem_DeletePaths_RootDirectory_Deletion_ErrorMessage);
        }

        var normalizedWorkingDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        if (string.Equals(normalizedPath, normalizedWorkingDirectory, PathContainment.SystemPathComparison))
        {
            throw new HandledException(
                new UnauthorizedAccessException(
                    "The chat working directory cannot be deleted by this tool."),
                LocaleKey.BuiltInChatPlugin_FileSystem_DeletePaths_WorkingDirectory_Deletion_ErrorMessage);
        }

        var target = EnsureFileSystemInfo(path);
        if (target.Attributes.HasFlag(FileAttributes.System))
        {
            throw new HandledException(
                new UnauthorizedAccessException(
                    "The requested path is marked as a system file or directory and cannot be deleted."),
                LocaleKey.BuiltInChatPlugin_FileSystem_DeletePaths_SystemFile_Deletion_ErrorMessage);
        }

        if (target.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new HandledException(
                new NotSupportedException(
                    $"The requested path is a symbolic link, junction, or other reparse point and cannot be deleted: '{path}'."),
                LocaleKey.HandledSystemException_NotSupported);
        }

        if (target is DirectoryInfo directory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!recursive && directory.EnumerateFileSystemInfos().Any())
            {
                throw new HandledException(
                    new IOException(
                        $"The directory '{path}' is not empty. Set recursive=true to delete its contents."),
                    LocaleKey.BuiltInChatPlugin_FileSystem_DeletePaths_NonEmptyDirectory_ErrorMessage);
            }
        }

        return target;
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
        if (Directory.Exists(path)) return;
        if (File.Exists(path))
        {
            throw new HandledException(
                new IOException(
                    $"The directory cannot be created because a file already exists at the requested path: '{path}'."),
                LocaleKey.HandledSystemException_IOException);
        }

        await RequestFileOperationConsentAsync(
            userInterface,
            chatContext,
            new FormattedDynamicLocaleKey(
                LocaleKey.BuiltInChatPlugin_FileSystem_CreateDirectory_CreateConsent_Header,
                new DirectLocaleKey(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)))),
            [path],
            null,
            cancellationToken);
        Directory.CreateDirectory(path);
    }

    [KernelFunction("apply_patch")]
    [Description(
        """
        Apply local text-file changes using this EXACT protocol:

        *** Begin Patch
        [one or more file operations]
        *** End Patch

        ## File operations

        *** Add File: <path>
        +<new line>

        *** Delete File: <path>

        *** Update File: <path>
        [*** Move to: <destination>]
        [one or more complete hunks]

        Each resolved file path MUST appear in exactly one file operation. Paths may be relative, absolute, or `file://` URIs.
        To edit several locations in one file, use a single `*** Update File` operation and place multiple complete `@@` hunks under it. MUST in top-to-bottom source order or they will fail.

        ## Update hunks

        Every update hunk begins with exactly one complete physical header line:
        1. Bare header (for most cases): `@@`
        2. Anchored header: `@@ <exact source line BEFORE the target>`

        A bare `@@` searches forward for the first matching context/removal lines, starting at or after the previous hunk's end. Line numbers and anchors are therefore normally unnecessary.
        DO NOT use an anchored header unless you MUST begin searching after a particular occurrence, such as when identical text appears earlier in the file and MUST be skipped.
        The anchor moves the search position to immediately after that exact source line. It is not part of the hunk body and cannot itself be modified by that hunk.
        DO NOT surround anchor text with quotes. Quotes would be matched literally.
        If the source line is indented, preserve that indentation after the single separator space following `@@`.

        ## Hunk body lines

        Every hunk body line starts with EXACTLY ONE marker, otherwise it is ILLEGAL:
         : unchanged context; the line remains in the file.
        -: existing text to remove.
        +: new text to add.

        Copy leading whitespace EXACTLY AFTER the marker, especially on `-` and `+` lines.
        Matching may tolerate whitespace differences in existing lines, but `+` lines are written EXACTLY as supplied.
        Even an empty body line must have a marker.
        Every `@@` starts a new independent hunk. Every hunk must contain at least one `+` or `-` line before the next `@@`, file operation, or patch terminator.

        ## Samples

        Replace existing text:
        @@
        -old text
        +new text

        Replace existing text with an anchor:
        @@ void Reset()
        -state = 0;
        +state = 1;

        Delete existing text:
        @@
        -text to delete

        Insert after an unchanged line:
        @@
         unchanged line
        +new text

        ## Examples of Errors

        The body line starts AFTER the anchor text, not on the same line:
        @@ state = 0;
        -state = 0;
        +state = 1;

        Missing a marker on a body line:
        @@
        state = 0;
        +state = 1;

        The old text is retained:
        @@
         old text
        +new text

        ## Search and append behavior

        Each bare `@@` searches for its context/removal lines at or after the previous hunk's end. It does not automatically append content to the file.

        For an addition-only hunk, use one of these forms:
        * Use `@@ <anchor>` to insert after a specific source line.
        * To append at the end of the file, use a bare `@@` followed by additions and `*** End of File`.

        Example append:
        @@
        +new line
        *** End of File
        """)]
    [DynamicLocaleKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_ApplyPatch_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_ApplyPatch_Description)]
    private async Task<PromptNode> ApplyPatchAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        [Description("The complete patch document, including the *** Begin Patch and *** End Patch markers.")]
        string? patch,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Applying a file patch with {CharacterCount} characters", patch?.Length ?? 0);
        if (string.IsNullOrWhiteSpace(patch))
        {
            throw new HandledException(
                new ArgumentException("The apply_patch tool requires a non-empty patch.", nameof(patch)),
                LocaleKey.BuiltInChatPlugin_FileSystem_ApplyPatch_ErrorMessage);
        }

        PatchPlan plan;
        try
        {
            var document = PatchParser.Parse(patch);
            plan = await PatchPlanBuilder.BuildAsync(
                document,
                chatContext.EnsureWorkingDirectory(),
                PatchLimits.Default,
                cancellationToken);
        }
        catch (Exception ex) when (ex is PatchParseException or PatchPlanException or PatchMatchException)
        {
            throw new HandledException(
                ex,
                LocaleKey.BuiltInChatPlugin_FileSystem_ApplyPatch_ErrorMessage,
                showDetails: true);
        }

        var references = CreatePatchReferences(plan);
        userInterface.ActivityPreview = new ChatPluginFileReferencesActivityPreview(references);

        using var review = PatchReviewSession.Create(plan);

        IReadOnlyList<PatchFileDecision> decisions;
        try
        {
            decisions = await review.ReviewAsync(
                (item, token) => RequestPatchFileDecisionAsync(userInterface, chatContext, item, token),
                userInterface.DisplaySink,
                cancellationToken);
        }
        catch (PatchReviewException ex)
        {
            throw new HandledException(
                ex,
                LocaleKey.BuiltInChatPlugin_FileSystem_ApplyPatch_ErrorMessage,
                showDetails: true);
        }

        PatchCommitResult result;
        try
        {
            result = await PatchCommitter.CommitAsync(
                plan,
                decisions,
                PatchLimits.Default,
                cancellationToken);
        }
        catch (PatchCommitException ex)
        {
            throw new HandledException(
                ex,
                LocaleKey.BuiltInChatPlugin_FileSystem_ApplyPatch_ErrorMessage,
                showDetails: true);
        }

        return new PromptTokenLimit(40000, FormatPatchCommitResult(result, plan));
    }

    private static ChatPluginFileReference[] CreatePatchReferences(PatchPlan plan)
    {
        var paths = new List<string>(plan.Files.Count * 2);
        foreach (var file in plan.Files)
        {
            paths.Add(file.SourcePath);
            if (file is PatchMovePlanFile move) paths.Add(move.DestinationPath);
        }

        return paths
            .AsValueEnumerable()
            .Distinct(PathContainment.SystemPathComparer)
            .Select(static path => new ChatPluginFileReference(path))
            .ToArray();
    }

    private Task<RequestConsentResult> RequestPatchFileDecisionAsync(
        IChatPluginUserInterface userInterface,
        ChatContext chatContext,
        PatchReviewItem item,
        CancellationToken cancellationToken)
    {
        var paths = item.File is PatchMovePlanFile move ? new[] { move.SourcePath, move.DestinationPath } : new[] { item.File.ReviewPath };
        return RequestFileOperationConsentResultAsync(
            userInterface,
            chatContext,
            new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_ApplyPatch_FileOperationReview_Header),
            paths,
            () => item.DisplayBlock,
            cancellationToken);
    }

    private static string FormatPatchCommitResult(PatchCommitResult result, PatchPlan plan)
    {
        var hasCommittedFile = false;
        var hasRejectedFile = false;
        var hasRejectedChange = false;
        var appliedAddedLineCount = 0;
        var appliedRemovedLineCount = 0;
        foreach (var fileResult in result.Files.AsValueEnumerable())
        {
            if (fileResult.Status is PatchCommitStatus.Committed) hasCommittedFile = true;
            if (fileResult.Status is PatchCommitStatus.RejectedByUser) hasRejectedFile = true;

            foreach (var change in fileResult.Decision.Changes.AsValueEnumerable())
            {
                if (!change.Accepted)
                {
                    hasRejectedChange = true;
                    continue;
                }

                if (fileResult.Status is not PatchCommitStatus.Committed) continue;
                appliedAddedLineCount += change.AddedLineCount;
                appliedRemovedLineCount += change.RemovedLineCount;
            }
        }

        var output = new StringBuilder(256 + result.Files.Count * 128);
        output
            .AppendLine(
                !result.Succeeded ?
                    "Patch was not fully applied." :
                    hasCommittedFile ?
                        "Patch applied successfully." :
                        "Patch completed with no file changes.")
            .Append("Applied line changes: ");
        AppendLineChangeSummary(output, appliedAddedLineCount, appliedRemovedLineCount);
        output.AppendLine(".");

        if (hasRejectedFile || hasRejectedChange)
        {
            output
                .AppendLine(
                    hasCommittedFile ?
                        "The patch parsed and matched successfully; some proposed changes were rejected during user review." :
                        "The patch parsed and matched successfully; the proposed changes were rejected during user review.")
                .AppendLine("Accepted/rejected counts below refer to review-generated text changes, not patch hunks.")
                .AppendLine("Rejected changes are user decisions, not patch syntax or matching errors, and were not written.")
                .AppendLine("Do not retry rejected changes unless the user requests it.");
        }

        AppendPatchMatchWarnings(output, plan, result);

        output.AppendLine("Files:");
        foreach (var file in result.Files)
        {
            var status = GetPatchCommitStatusText(file.Status);
            var fileAddedLineCount = 0;
            var fileRemovedLineCount = 0;
            var acceptedChangeCount = 0;
            var rejectedChangeCount = 0;
            foreach (var change in file.Decision.Changes.AsValueEnumerable())
            {
                if (change.Accepted)
                {
                    acceptedChangeCount++;
                    if (file.Status is not PatchCommitStatus.Committed) continue;
                    fileAddedLineCount += change.AddedLineCount;
                    fileRemovedLineCount += change.RemovedLineCount;
                }
                else
                {
                    rejectedChangeCount++;
                }
            }

            output.Append("- ").Append(status).Append(": ").Append(file.Path).Append(" — ");
            AppendLineChangeSummary(output, fileAddedLineCount, fileRemovedLineCount);
            if (file.Decision.Changes.Count > 0)
            {
                output
                    .Append("; review-generated changes: ")
                    .Append(acceptedChangeCount)
                    .Append(" accepted, ")
                    .Append(rejectedChangeCount)
                    .Append(" rejected");
            }

            if (file.Decision is PatchRejectedFileDecision { Reason: { Length: > 0 } rejectionReason })
            {
                output.Append(" — user reason: ").Append(rejectionReason);
            }

            if (file.Error is { Length: > 0 } errorMessage)
            {
                output.Append(" — ").Append(errorMessage);
            }

            output.AppendLine();

            foreach (var change in file.Decision.Changes.Where(static change => !string.IsNullOrWhiteSpace(change.Comment)))
            {
                var selection = change.Accepted ? "accepted" : "rejected";
                output
                    .Append("  user comment on ").Append(selection).Append(" change ")
                    .Append(change.Id.AsSpan(0, Math.Min(6, change.Id.Length))).Append(": ")
                    .AppendLine(change.Comment);
            }
        }

        if (result.Error is { Length: > 0 } && result.Files.All(static file => file.Error is null))
        {
            output.Append("Error: ").AppendLine(result.Error);
        }

        return output.TrimEnd().ToString();
    }

    private static void AppendPatchMatchWarnings(StringBuilder output, PatchPlan plan, PatchCommitResult result)
    {
        if (!plan.Files.AsValueEnumerable().Any(static file => file.MatchDiagnostics.Count > 0)) return;

        var resultsByPath = result.Files.ToDictionary(
            static file => file.Path,
            PathContainment.SystemPathComparer);
        output.AppendLine("Warnings: tolerant matching was used while planning this patch:");
        foreach (var file in plan.Files)
        {
            if (file.MatchDiagnostics.Count == 0) continue;

            var status = resultsByPath.TryGetValue(file.ReviewPath, out var fileResult) ?
                GetPatchCommitStatusText(fileResult.Status) :
                "unknown status";
            foreach (var diagnostic in file.MatchDiagnostics)
            {
                output
                    .Append("- ").Append(status).Append(": ")
                    .Append(file.ReviewPath).Append(", ")
                    .Append(IsContextMatch(diagnostic.Kind) ? "anchored hunk #" : "hunk #")
                    .Append(diagnostic.HunkNumber)
                    .Append(" (patch header line ").Append(diagnostic.HeaderLineNumber).Append(") matched ")
                    .AppendLine(DescribePatchMatchFallback(diagnostic.Kind));
            }
        }

        output
            .AppendLine("Added lines were kept exactly as supplied; tolerant matching does not repair their whitespace or punctuation.")
            .AppendLine("For committed files, re-read the affected lines and verify indentation and intended text.")
            .AppendLine("These were successful fallback matches, not patch errors. Do not retry automatically.");
    }

    private static string DescribePatchMatchFallback(PatchMatchKind kind) => kind switch
    {
        PatchMatchKind.TrailingWhitespaceFallback or PatchMatchKind.ContextTrailingWhitespaceFallback =>
            "after ignoring trailing whitespace.",
        PatchMatchKind.OuterWhitespaceFallback or PatchMatchKind.ContextOuterWhitespaceFallback =>
            "after ignoring leading and trailing whitespace.",
        PatchMatchKind.UnicodeCompatibilityFallback or PatchMatchKind.ContextUnicodeCompatibilityFallback =>
            "after normalizing compatible Unicode whitespace or punctuation.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The match kind is not a tolerant fallback.")
    };

    private static bool IsContextMatch(PatchMatchKind kind) => kind is
        PatchMatchKind.ContextTrailingWhitespaceFallback or
        PatchMatchKind.ContextOuterWhitespaceFallback or
        PatchMatchKind.ContextUnicodeCompatibilityFallback;

    private static string GetPatchCommitStatusText(PatchCommitStatus status) => status switch
    {
        PatchCommitStatus.Committed => "committed",
        PatchCommitStatus.NoChanges => "no changes",
        PatchCommitStatus.RejectedByUser => "rejected by user",
        PatchCommitStatus.Conflict => "conflict",
        PatchCommitStatus.Failed => "failed",
        PatchCommitStatus.NotAttempted => "not attempted",
        _ => status.ToString()
    };

    private static void AppendLineChangeSummary(StringBuilder output, int addedLineCount, int removedLineCount)
    {
        output
            .Append(addedLineCount)
            .Append(' ')
            .Append(Pluralize(addedLineCount, "line", "lines"))
            .Append(" added, ")
            .Append(removedLineCount)
            .Append(' ')
            .Append(Pluralize(removedLineCount, "line", "lines"))
            .Append(" deleted");
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
            result.Metadata.Where(static item => item.Value is not null).Select(static item => $", {item.Key}={item.Value}"));
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
            // Keep the continuation instruction while content lines are pruned. Their numeric
            // prefixes still let the model continue after the last line it actually received.
            output.Add(
                new PromptText($"{Environment.NewLine}[More content is available. Continue with offset={result.NextOffset}.]{Environment.NewLine}")
                    .WithPriority(int.MaxValue));
        }

        return output;
    }

    private static PromptTokenLimit BuildSearchOutput(
        IChatPluginDisplaySink displaySink,
        List<FileContentMatch> matches,
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
                    .ToArray()))
            .ToArray();

        AllocateSearchLines(files, maxResults);
        var shownFiles = files.AsValueEnumerable().Where(static file => file.ShownCount > 0).ToArray();
        var totalLines = files.AsValueEnumerable().Sum(static file => file.Lines.Length);
        var shownLines = shownFiles.AsValueEnumerable().Sum(static file => file.ShownCount);
        var shownOccurrences = shownFiles.AsValueEnumerable().Sum(static file =>
            file.Lines.AsValueEnumerable().Take(file.ShownCount).Sum(static line => line.Matches.Count));
        var qualifier = shownLines < totalLines ?
            $" (showing {shownOccurrences} {Pluralize(shownOccurrences, "occurrence", "occurrences")} on " +
            $"{shownLines} {Pluralize(shownLines, "line", "lines")} in " +
            $"{shownFiles.Length} {Pluralize(shownFiles.Length, "file", "files")})" :
            string.Empty;
        if (limitHit) qualifier += " (search limit reached)";

        var output = new PromptTokenLimit(40000)
        {
            $"Found {matches.Count} {Pluralize(matches.Count, "occurrence", "occurrences")} on " +
            $"{totalLines} matching {Pluralize(totalLines, "line", "lines")} in " +
            $"{files.Length} {Pluralize(files.Length, "file", "files")} for \"{pattern}\"{qualifier}{Environment.NewLine}"
        };

        for (var fileIndex = 0; fileIndex < shownFiles.Length; fileIndex++)
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

            if (file.ShownCount < file.Lines.Length)
            {
                lines.Add($"... ({file.Lines.Length - file.ShownCount} more matching line(s) in this file)");
            }

            displaySink.AppendFileReferences(new ChatPluginFileReference(file.Path, locations: locations));

            // Matching lines stay together so the renderer removes complete file blocks.
            output.Add(new PromptText(string.Join(Environment.NewLine, lines)).WithPriority(1000 - fileIndex));
        }

        return output;
    }

    private static void AllocateSearchLines(IReadOnlyList<SearchFileGroup> files, int maxResults)
    {
        var keptFiles = files.AsValueEnumerable().Take(maxResults).ToArray();
        foreach (var file in keptFiles.AsValueEnumerable()) file.ShownCount = 1;

        var remaining = maxResults - keptFiles.Length;
        var capacity = keptFiles.AsValueEnumerable().Sum(static file => file.Lines.Length - 1);
        if (remaining <= 0 || capacity <= 0) return;

        var allocations = keptFiles
            .AsValueEnumerable()
            .Select(file =>
            {
                // ReSharper disable once AccessToModifiedClosure
                var exact = (double)(file.Lines.Length - 1) / capacity * remaining;
                var added = Math.Min(file.Lines.Length - 1, (int)Math.Floor(exact));
                return new SearchLineAllocation(file, added, exact - Math.Floor(exact));
            })
            .ToArray();
        foreach (var allocation in allocations.AsValueEnumerable()) allocation.File.ShownCount += allocation.Added;

        remaining -= allocations.AsValueEnumerable().Sum(static allocation => allocation.Added);
        foreach (var allocation in allocations.AsValueEnumerable().OrderByDescending(static allocation => allocation.Remainder))
        {
            if (remaining == 0) break;
            if (allocation.File.ShownCount >= allocation.File.Lines.Length) continue;
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
            throw new HandledException(
                new ArgumentException($"The supplied file-name pattern is invalid: {ex.Message}", ex),
                LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPattern_ErrorMessage);
        }
    }

    private static string ExpandLocalPath(ChatContext chatContext, string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            throw new HandledException(
                new NotSupportedException(
                    "The requested path uses a non-file URI scheme. This file-system tool supports local paths and file:// URIs only."),
                LocaleKey.BuiltInChatPlugin_FileSystem_LocalPathOnly_ErrorMessage);
        }

        try
        {
            return ExpandFullPath(chatContext.EnsureWorkingDirectory(), uri?.IsFile == true ? uri.LocalPath : path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new HandledException(
                new ArgumentException($"The path '{path}' could not be resolved as a valid local path: {ex.Message}", ex),
                LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPath_ErrorMessage);
        }
    }

    internal static string ExpandFullPath(string workingDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new HandledException(
                new ArgumentException(
                    "The path argument is empty. Provide a local file or directory path.",
                    nameof(path)),
                LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPath_ErrorMessage);
        }

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new HandledException(
                new ArgumentException(
                    "The chat working directory is empty, so the requested relative path cannot be resolved.",
                    nameof(workingDirectory)),
                LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPath_ErrorMessage);
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path), workingDirectory);
    }

    private static ChatPluginContainerDisplayBlock CreateFileTransferContent(string source, string destination, FileTransferOperation operation) =>
        new()
        {
            new ChatPluginDynamicLocaleKeyDisplayBlock(
                new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_TransferPath_Consent_From),
                "Muted"),
            new ChatPluginFileReferencesDisplayBlock(new ChatPluginFileReference(source)),
            new ChatPluginDynamicLocaleKeyDisplayBlock(
                new DynamicLocaleKey(
                    operation is FileTransferOperation.Copy ?
                        LocaleKey.BuiltInChatPlugin_FileSystem_TransferPath_CopyTo :
                        LocaleKey.BuiltInChatPlugin_FileSystem_TransferPath_MoveTo),
                "Muted"),
            new ChatPluginFileReferencesDisplayBlock(new ChatPluginFileReference(destination))
        };

    private static void EnsureDirectoryCanBeCopied(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new HandledException(
                new NotSupportedException(
                    $"The directory copy was not performed because '{directory.FullName}' is a symbolic link or directory junction. Copying linked directory trees is not supported."),
                LocaleKey.HandledSystemException_NotSupported);
        }

        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new HandledException(
                    new NotSupportedException(
                        $"The directory copy was not performed because it contains a symbolic link or directory junction: '{entry.FullName}'. Copying linked directory trees is not supported."),
                    LocaleKey.HandledSystemException_NotSupported);
            }

            if (entry is DirectoryInfo childDirectory)
            {
                EnsureDirectoryCanBeCopied(childDirectory, cancellationToken);
            }
        }
    }

    private static void CopyDirectory(DirectoryInfo source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                CopyDirectory(directory, target, cancellationToken);
            }
            else
            {
                ((FileInfo)entry).CopyTo(target, overwrite: false);
            }
        }
    }

    /// <summary>
    /// Creates the compact request preview used by file operations. The detailed file reference is
    /// still appended to the display sink separately, so this helper never duplicates durable output
    /// or exposes operation results in the running activity row.
    /// </summary>
    private static ChatPluginFileReferencesActivityPreview CreateFilePreview(string path, IDynamicLocaleKey? prefixKey = null) =>
        new([new ChatPluginFileReference(path)], prefixKey);

    private async Task RequestFileOperationConsentAsync(
        IChatPluginUserInterface userInterface,
        ChatContext chatContext,
        IDynamicLocaleKey headerKey,
        string[] paths,
        Func<ChatPluginDisplayBlock>? contentFactory,
        CancellationToken cancellationToken,
        bool forceConsent = false)
    {
        var consent = await RequestFileOperationConsentResultAsync(
            userInterface,
            chatContext,
            headerKey,
            paths,
            contentFactory,
            cancellationToken,
            forceConsent);
        if (consent) return;

        throw new HandledException(
            new UnauthorizedAccessException(
                consent.FormatReason(
                    "The user denied the file-operation approval request, so the operation was not performed.")),
            LocaleKey.BuiltInChatPlugin_FileSystem_ConsentDenied_ErrorMessage);
    }

    /// <summary>
    /// Returns the effective file-operation approval after applying tool, working-directory, and
    /// persisted path rules, requesting consent only when none of those rules covers every path.
    /// </summary>
    private async Task<RequestConsentResult> RequestFileOperationConsentResultAsync(
        IChatPluginUserInterface userInterface,
        ChatContext chatContext,
        IDynamicLocaleKey headerKey,
        string[] paths,
        Func<ChatPluginDisplayBlock>? contentFactory,
        CancellationToken cancellationToken,
        bool forceConsent = false)
    {
        if (chatContext.FunctionCallContext.Value?.BypassesApproval is true) return RequestConsentResult.Accept;

        var workingDirectory = chatContext.EnsureWorkingDirectory();
        if (!forceConsent && paths.Length > 0 &&
            paths.AsValueEnumerable().All(path => Path.IsPathFullyQualified(path) && PathContainment.IsInsideDirectory(path, workingDirectory)))
        {
            return RequestConsentResult.Accept;
        }

        if (!forceConsent && _fileSystemSettings.ArePathsApproved(paths)) return RequestConsentResult.Accept;

        var content = contentFactory?.Invoke() ??
            new ChatPluginFileReferencesDisplayBlock(paths.AsValueEnumerable().Select(path => new ChatPluginFileReference(path)).ToArray())
            {
                TotalReferenceCount = paths.Length
            };

        // Build custom options
        var commonParentDirectory = PathContainment.GetCommonParentDirectory(paths);
        var customOptions = new List<RequestConsentCustomOption>
        {
            new(
                FileSystemConsentOption.ExactPaths,
                new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_Consent_AllowExactPaths),
                LucideIconKind.FileCheck)
        };
        if (commonParentDirectory is not null)
        {
            customOptions.Add(
                new RequestConsentCustomOption(
                    FileSystemConsentOption.ParentDirectories,
                    new FormattedDynamicLocaleKey(
                        LocaleKey.BuiltInChatPlugin_FileSystem_Consent_AllowParentDirectories,
                        new DirectLocaleKey(commonParentDirectory)),
                    LucideIconKind.FolderCheck));
        }
        if (CanPickCustomDirectory())
        {
            customOptions.Add(
                new RequestConsentCustomOption(
                    FileSystemConsentOption.CustomDirectory,
                    new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_FileSystem_Consent_AllowCustomDirectory),
                    LucideIconKind.FolderCog));
        }

        var consent = await userInterface.RequestConsentAsync(
            Guid.CreateVersion7().ToString(), // Random, not remembered
            headerKey,
            content,
            RequestConsentRememberMasks.AllowOnce,
            customOptions,
            cancellationToken: cancellationToken);

        if (!consent) return consent;

        if (consent.CustomOption?.Key is not FileSystemConsentOption option) return consent;

        switch (option)
        {
            case FileSystemConsentOption.ExactPaths:
            {
                foreach (var path in paths) _fileSystemSettings.AddApprovalPath(path);
                break;
            }
            case FileSystemConsentOption.ParentDirectories when commonParentDirectory is not null:
            {
                _fileSystemSettings.AddApprovalPath(CreateDirectoryApprovalPattern(commonParentDirectory));
                break;
            }
            case FileSystemConsentOption.CustomDirectory:
            {
                string? selectedDirectory;
                try
                {
                    var folders = await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        var options = new FolderPickerOpenOptions { AllowMultiple = false };
                        if (!commonParentDirectory.IsNullOrWhiteSpace())
                        {
                            options.SuggestedStartLocation = await App.StorageProvider.TryGetFolderFromPathAsync(commonParentDirectory);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        return await App.StorageProvider.OpenFolderPickerAsync(options);
                    });
                    selectedDirectory = folders.FirstOrDefault()?.Path.LocalPath;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new HandledException(
                        new InvalidOperationException(
                            $"The user selected custom-folder approval, but the folder picker failed to open: {ex.Message} No approval was saved and the operation was not performed.",
                            ex),
                        LocaleKey.BuiltInChatPlugin_FileSystem_ConsentDenied_ErrorMessage);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (selectedDirectory.IsNullOrWhiteSpace())
                {
                    throw new HandledException(
                        new UnauthorizedAccessException(
                            "The user selected custom-folder approval and then canceled folder selection. No approval was saved and the operation was not performed."),
                        LocaleKey.BuiltInChatPlugin_FileSystem_ConsentDenied_ErrorMessage);
                }

                if (!paths.AsValueEnumerable().All(path => PathContainment.IsInsideDirectory(path, selectedDirectory)))
                {
                    throw new HandledException(
                        new UnauthorizedAccessException(
                            "The user chose a folder for always-allow approval, but that folder does not contain every path required by this operation. No approval was saved and the operation was not performed."),
                        LocaleKey.BuiltInChatPlugin_FileSystem_ConsentDenied_ErrorMessage);
                }

                _fileSystemSettings.AddApprovalPath(CreateDirectoryApprovalPattern(selectedDirectory));
                break;
            }
        }

        return consent;

        bool CanPickCustomDirectory()
        {
            try
            {
                return App.StorageProvider.CanPickFolder;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Failed to check if folder picking is supported.");
                return false;
            }
        }

        static string CreateDirectoryApprovalPattern(string path)
        {
            var normalized = FileSystemApprovalPath.Normalize(Path.GetFullPath(path));
            return normalized.TrimEnd('/') + "/**";
        }
    }

    private enum FileSystemConsentOption
    {
        ExactPaths,
        ParentDirectories,
        CustomDirectory
    }

    private static FileSystemInfo EnsureFileSystemInfo(string path)
    {
        if (File.Exists(path)) return new FileInfo(path);
        if (Directory.Exists(path)) return new DirectoryInfo(path);
        throw new HandledException(
            new FileNotFoundException($"The requested path does not exist: '{path}'.", path),
            LocaleKey.BuiltInChatPlugin_FileSystem_EnsureFileSystemInfo_PathNotExist_ErrorMessage);
    }

    /// <summary>
    /// Holds matching logical lines and their output allocation for one file.
    /// </summary>
    private sealed class SearchFileGroup(string path, SearchLineGroup[] lines)
    {
        public string Path { get; } = path;
        public SearchLineGroup[] Lines { get; } = lines;
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

    [JsonConverter(typeof(JsonStringEnumConverter))]
    private enum FileTransferOperation
    {
        Copy,
        Move
    }

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

    /// <summary>
    /// Renders both endpoints and the requested direction of a file transfer.
    /// </summary>
    private sealed class FileTransferRenderer : IFriendlyFunctionCallContentRenderer
    {
        public ChatPluginDisplayBlock? Render(KernelArguments arguments)
        {
            if (!arguments.TryGetValue("source", out var sourceValue) || sourceValue is not string source ||
                !arguments.TryGetValue("destination", out var destinationValue) || destinationValue is not string destination ||
                !arguments.TryGetValue("operation", out var operationValue) ||
                !Enum.TryParse<FileTransferOperation>(operationValue?.ToString(), ignoreCase: true, out var operation))
            {
                return null;
            }

            return CreateFileTransferContent(source, destination, operation);
        }
    }
}