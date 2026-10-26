using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Terminal;
using Lucide.Avalonia;
using MessagePack;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Used to represent a block of content displayed by a chat plugin.
/// </summary>
[MessagePackObject]
[Union(0, typeof(ChatPluginContainerDisplayBlock))]
[Union(1, typeof(ChatPluginTextDisplayBlock))]
[Union(2, typeof(ChatPluginDynamicLocaleKeyDisplayBlock))]
[Union(3, typeof(ChatPluginMarkdownDisplayBlock))]
[Union(4, typeof(ChatPluginProgressDisplayBlock))]
[Union(5, typeof(ChatPluginFileReferencesDisplayBlock))]
[Union(6, typeof(ChatPluginFileDifferenceDisplayBlock))]
[Union(7, typeof(ChatPluginUrlsDisplayBlock))]
[Union(8, typeof(ChatPluginSeparatorDisplayBlock))]
[Union(9, typeof(ChatPluginCodeBlockDisplayBlock))]
[Union(10, typeof(ChatPluginSubagentDisplayBlock))]
[Union(11, typeof(ChatPluginTerminalDisplayBlock))]
public abstract partial class ChatPluginDisplayBlock
{
    /// <summary>
    /// Gets the direct persistence and turn-invalidation destination, if this block belongs to chat.
    /// Standalone consent/review blocks remain usable without an owner.
    /// </summary>
    [IgnoreMember]
    public ChatMessageNode? Owner
    {
        get;
        internal set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            field = value;
            OnOwnerChanged(value);
        }
    }

    /// <summary>
    /// Assigns descendant ownership or reconnects an explicit embedded-data durability bridge.
    /// </summary>
    protected virtual void OnOwnerChanged(ChatMessageNode? owner) { }

    /// <summary>
    /// Signals changed values to a realized standalone block, such as consent or terminal status.
    /// Chat persistence and turn rendering are notified directly through Owner.
    /// </summary>
    public event EventHandler? Invalidated;

    protected void NotifyChanged()
    {
        Owner?.NotifyMessageChanged(isDurable: true);
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    protected void SetProperty<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        NotifyChanged();
    }
}

/// <summary>
/// Represents a container block that can hold other display blocks.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginContainerDisplayBlock : ChatPluginDisplayBlock, IEnumerable<ChatPluginDisplayBlock>, IDisposable
{
    /// <summary>
    /// Gets a stable reference snapshot of this container's children.
    /// </summary>
    [IgnoreMember]
    public IReadOnlyList<ChatPluginDisplayBlock> Children => _displaySink.Items;

    [IgnoreMember]
    public ChatPluginDisplaySink DisplaySink => _displaySink;

    [Key(0)] private readonly ChatPluginDisplaySink _displaySink;

    [SerializationConstructor]
    private ChatPluginContainerDisplayBlock(ChatPluginDisplaySink displaySink)
    {
        _displaySink = displaySink;
        _displaySink.Changed += HandleChildrenChanged;
    }

    private void HandleChildrenChanged(object? sender, EventArgs e) => NotifyChanged();

    /// <inheritdoc />
    protected override void OnOwnerChanged(ChatMessageNode? owner) => _displaySink.SetOwner(owner);

    public ChatPluginContainerDisplayBlock() : this(new ChatPluginDisplaySink()) { }

    public void Add(ChatPluginDisplayBlock block) => _displaySink.Add(block);

    public IEnumerator<ChatPluginDisplayBlock> GetEnumerator()
    {
        return _displaySink.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return ((IEnumerable)_displaySink).GetEnumerator();
    }

    public void Dispose()
    {
        _displaySink.Changed -= HandleChildrenChanged;
        _displaySink.Dispose();
    }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginTextDisplayBlock(string text, string? className = null) : ChatPluginDisplayBlock
{
    [Key(0)]
    public string Text { get; } = text;

    [Key(1)]
    public string? ClassName { get; } = className;
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginDynamicLocaleKeyDisplayBlock(IDynamicLocaleKey key, string? className = null) : ChatPluginDisplayBlock
{
    [Key(0)]
    public IDynamicLocaleKey Key { get; } = key;

    [Key(1)]
    public string? ClassName { get; } = className;
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginMarkdownDisplayBlock : ChatPluginDisplayBlock, IDisposable
{
    public ChatTextBuffer MarkdownBuilder { get; } = new();

    [Key(0)]
    private string Markdown
    {
        get => MarkdownBuilder.ToString();
        set => MarkdownBuilder.Clear().Append(value);
    }

    public ChatPluginMarkdownDisplayBlock()
    {
        MarkdownBuilder.Changed += HandleMarkdownBuilderChanged;
    }

    private void HandleMarkdownBuilderChanged(object? sender, EventArgs e)
    {
        NotifyChanged();
    }

    public void Dispose()
    {
        MarkdownBuilder.Changed -= HandleMarkdownBuilderChanged;
    }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginProgressDisplayBlock(IDynamicLocaleKey headerKey) : ChatPluginDisplayBlock, IProgress<double>
{
    /// <summary>
    /// Gets a synchronous reporter; reported values reach the model before Report returns.
    /// </summary>
    [IgnoreMember]
    public IProgress<double> ProgressReporter => this;

    /// <inheritdoc />
    public void Report(double value) => Progress = value;

    [Key(0)]
    public IDynamicLocaleKey HeaderKey { get; } = headerKey;

    [Key(1)]
    public double Progress
    {
        get;
        set => SetProperty(ref field, value);
    }
}

/// <summary>
/// Represents a reference to a file or folder in a chat plugin display block.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public partial class ChatPluginFileReference(
    string fullPath,
    IDynamicLocaleKey? displayNameKey = null,
    IReadOnlySet<ChatPluginFileReferenceLocation>? locations = null
)
{
    [Key(0)]
    public string FullPath { get; } = fullPath;

    [Key(1)]
    public IDynamicLocaleKey? DisplayNameKey { get; } = displayNameKey;

    [Key(2)]
    public IReadOnlySet<ChatPluginFileReferenceLocation>? Locations { get; } = locations;

    [IgnoreMember]
    public Task<LucideIconKind> IconAsync => Task.Run(() =>
    {
        if (Directory.Exists(FullPath)) return LucideIconKind.Folder;
        return Path.GetExtension(FullPath).ToLowerInvariant() switch
        {
            ".cs" or ".rs" or ".py" or ".js" or ".ts" or ".cpp" or ".c" or ".html" or ".css" or ".java" => LucideIconKind.FileCode,
            ".txt" or ".md" or ".markdown" or ".doc" or ".docx" or ".rtf" => LucideIconKind.FileText,
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => LucideIconKind.FileImage,
            ".mp4" or ".avi" or ".mov" or ".wmv" or ".mkv" => LucideIconKind.FileVideoCamera,
            ".sh" or ".exe" or ".bat" or ".cmd" or ".ps1" => LucideIconKind.FileTerminal,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => LucideIconKind.FileArchive,
            ".mp3" or ".wav" or ".flac" or ".aac" => LucideIconKind.FileMusic,
            _ => LucideIconKind.File
        };
    });

    [RelayCommand]
    private void OpenFileLocation()
    {
        ServiceLocator.Resolve<INativeHelper>().OpenFileLocation(FullPath);
    }
}

/// <summary>
/// Represents a specific location within a file reference with line and column number.
/// </summary>
/// <param name="Line"></param>
/// <param name="Column"></param>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public readonly partial record struct ChatPluginFileReferenceLocation(
    [property: Key(0)] int Line,
    [property: Key(1)] int Column
);

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginFileReferencesDisplayBlock(params IReadOnlyList<ChatPluginFileReference> references) : ChatPluginDisplayBlock
{
    [Key(0)]
    public IReadOnlyList<ChatPluginFileReference> References { get; } = references.AsValueEnumerable().Take(10).ToArray();

    [Key(1)]
    public int TotalReferenceCount
    {
        get;
        set => SetProperty(ref field, value);
    } = references.Count;

    [IgnoreMember]
    public bool HasMoreReferences => TotalReferenceCount > References.Count;
}

/// <summary>
/// Presents a file operation as an interactive consent review or a completed lightweight summary.
/// </summary>
/// <remarks>
/// Detailed text and change objects exist only while consent is pending. <see cref="CompleteReview"/>
/// removes them before the block is appended to a durable DisplaySink.
/// </remarks>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginFileDifferenceDisplayBlock : ChatPluginDisplayBlock
{
    [Key(0)]
    public TextDifference? Difference
    {
        get;
        private set => SetProperty(ref field, value);
    }

    [Key(1)]
    public string? OriginalText
    {
        get;
        private set => SetProperty(ref field, value);
    }

    [Key(2)]
    public TextDifferenceReviewKind ReviewKind { get; }

    [Key(3)]
    public string? SourcePath { get; }

    [Key(4)]
    public string FilePath { get; }

    [Key(5)]
    public int AddedLineCount { get; }

    [Key(6)]
    public int RemovedLineCount { get; }

    [IgnoreMember]
    public bool HasLineChanges => AddedLineCount > 0 || RemovedLineCount > 0;

    [IgnoreMember]
    public bool CanReview => Difference is { TotalChangesCount: > 0 } && OriginalText is not null;

    /// <summary>
    /// Initializes a file-difference block for consent review.
    /// </summary>
    public ChatPluginFileDifferenceDisplayBlock(
        TextDifference difference,
        string originalText,
        TextDifferenceReviewKind reviewKind,
        string? sourcePath)
    {
        Difference = difference;
        OriginalText = originalText;
        ReviewKind = reviewKind;
        SourcePath = sourcePath;
        FilePath = difference.FilePath;
        AddedLineCount = CountAddedLines(difference);
        RemovedLineCount = CountRemovedLines(difference, originalText);
    }

    [SerializationConstructor]
    private ChatPluginFileDifferenceDisplayBlock(
        TextDifference? difference,
        string? originalText,
        TextDifferenceReviewKind reviewKind,
        string? sourcePath,
        string? filePath,
        int addedLineCount,
        int removedLineCount)
    {
        Difference = null;
        OriginalText = null;
        ReviewKind = reviewKind;
        SourcePath = sourcePath;
        FilePath = filePath ?? difference?.FilePath ?? sourcePath ?? string.Empty;
        AddedLineCount = addedLineCount != 0 || difference is null ?
            addedLineCount :
            CountAddedLines(difference);
        RemovedLineCount = removedLineCount != 0 || difference is null || originalText is null ?
            removedLineCount :
            CountRemovedLines(difference, originalText);
    }

    /// <summary>
    /// Releases detailed review content before this block becomes durable DisplaySink output.
    /// </summary>
    internal void CompleteReview()
    {
        Difference = null;
        OriginalText = null;
    }

    private static int CountAddedLines(TextDifference difference) => difference.GetFilteredChanges(false)
        .AsValueEnumerable()
        .Where(static change => change.Kind is TextChangeKind.Insert or TextChangeKind.Replace)
        .Sum(static change => TextDifference.CountLines(change.NewText ?? string.Empty));

    private static int CountRemovedLines(TextDifference difference, string originalText) => difference.GetFilteredChanges(false)
        .AsValueEnumerable()
        .Where(static change => change.Kind is TextChangeKind.Delete or TextChangeKind.Replace)
        .Sum(change => TextDifference.CountLines(change.GetOriginalSlice(originalText)));
}

/// <summary>
/// Represents a URL with an optional display name key. Usage example: web search results.
/// </summary>
/// <param name="url"></param>
/// <param name="displayNameKey"></param>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginUrl(string? url, IDynamicLocaleKey displayNameKey)
{
    [Key(0)]
    public string? Url { get; } = url;

    [Key(1)]
    public IDynamicLocaleKey DisplayNameKey { get; } = displayNameKey;

    /// <summary>
    /// The index of this URL in the original list, if applicable.
    /// Useful to let the LLM refer to the origin of the answer.
    /// </summary>
    [Key(2)]
    public int Index { get; set; }
}

/// <summary>
/// Represents a display block containing multiple URLs.
/// </summary>
/// <param name="urls"></param>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginUrlsDisplayBlock(params IReadOnlyList<ChatPluginUrl> urls) : ChatPluginDisplayBlock
{
    [Key(0)]
    public IReadOnlyList<ChatPluginUrl> Urls { get; } = urls;
}

/// <summary>
/// Represents a separator display block.
/// </summary>
/// <param name="thickness"></param>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginSeparatorDisplayBlock(double thickness = 1.0d) : ChatPluginDisplayBlock
{
    [Key(0)]
    public double Thickness { get; } = thickness;
}

/// <summary>
/// Represents a code display block with syntax highlighting.
/// </summary>
/// <param name="code"></param>
/// <param name="language"></param>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginCodeBlockDisplayBlock(string code, string? language = null) : ChatPluginDisplayBlock
{
    [Key(0)]
    public string Code { get; } = code;

    [Key(1)]
    public string? Language { get; } = language;
}

/// <summary>
/// Displays an embedded conversation while retaining its persisted data.
/// </summary>
/// <remarks>
/// Owns the ChatData and will dispose it when this block is disposed. This is important to prevent memory leaks and ensure that resources are released properly.
/// </remarks>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginSubagentDisplayBlock : ChatPluginDisplayBlock, IDisposable
{
    [Key(0)]
    public ChatData ChatData { get; }

    /// <summary>
    /// Owns embedded conversation data and bridges its durable changes to this block's current owner.
    /// </summary>
    public ChatPluginSubagentDisplayBlock(ChatData chatData)
    {
        ChatData = chatData;
        ChatData.DurableChanged += HandleChildChanged;
        ChatData.Metadata.PropertyChanged += HandleMetadataChanged;
    }

    private void HandleChildChanged(object? sender, EventArgs e) => NotifyChanged();

    private void HandleMetadataChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMetadata.Topic))
        {
            HandleChildChanged(sender, e);
        }
    }

    public void Dispose()
    {
        Owner = null;
        ChatData.DurableChanged -= HandleChildChanged;
        ChatData.Metadata.PropertyChanged -= HandleMetadataChanged;
        ChatData.Dispose();
    }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginTerminalDisplayBlock : ChatPluginDisplayBlock
{
    [Key(0)]
    public ShellType ShellType { get; }

    [Key(1)]
    public string? Command { get; }

    [Key(2)]
    public DateTimeOffset CreatedAt { get; }

    [IgnoreMember]
    public TerminalRun? Run { get; }

    [Key(3)]
    public int? ExitCode
    {
        get;
        private set => SetProperty(ref field, value);
    }

    [Key(4)]
    public DateTimeOffset? FinishedAt
    {
        get;
        private set => SetProperty(ref field, value);
    }

    [IgnoreMember]
    public TimeSpan Elapsed => (FinishedAt ?? DateTimeOffset.UtcNow) - CreatedAt;

    [IgnoreMember] private TerminalSession? _session;

    public ChatPluginTerminalDisplayBlock(ShellType shellType, TerminalRun run, TerminalSession session)
    {
        ShellType = shellType;
        Command = run.CommandLine;
        Run = run;
        _session = session;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    [SerializationConstructor]
    private ChatPluginTerminalDisplayBlock(ShellType shellType, string command, DateTimeOffset createdAt)
    {
        ShellType = shellType;
        Command = command;
        CreatedAt = createdAt;
    }

    public ValueTask WriteInputAsync(string input, CancellationToken cancellationToken = default)
    {
        return _session?.WriteInputAsync(input, cancellationToken) ?? default;
    }

    /// <summary>
    /// Forwards encoded terminal input without re-encoding it.
    /// </summary>
    public Task WriteInputAsync(ReadOnlySpan<byte> input, CancellationToken cancellationToken = default)
    {
        return _session?.WriteInputAsync(input, cancellationToken) ?? Task.CompletedTask;
    }

    public ValueTask WritePasteAsync(string text, CancellationToken cancellationToken = default)
    {
        return _session?.WritePasteAsync(text, cancellationToken) ?? default;
    }

    public void Complete(int? exitCode)
    {
        _session = null;
        ExitCode = exitCode;
        FinishedAt = DateTimeOffset.UtcNow;
    }
}