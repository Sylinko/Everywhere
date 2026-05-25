using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Terminal;
using Lucide.Avalonia;
using MessagePack;
using ZLinq;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Used to represent a block of content displayed by a chat plugin.
/// </summary>
[MessagePackObject]
[Union(0, typeof(ChatPluginContainerDisplayBlock))]
[Union(1, typeof(ChatPluginTextDisplayBlock))]
[Union(2, typeof(ChatPluginDynamicResourceKeyDisplayBlock))]
[Union(3, typeof(ChatPluginMarkdownDisplayBlock))]
[Union(4, typeof(ChatPluginProgressDisplayBlock))]
[Union(5, typeof(ChatPluginFileReferencesDisplayBlock))]
[Union(6, typeof(ChatPluginFileDifferenceDisplayBlock))]
[Union(7, typeof(ChatPluginUrlsDisplayBlock))]
[Union(8, typeof(ChatPluginSeparatorDisplayBlock))]
[Union(9, typeof(ChatPluginCodeBlockDisplayBlock))]
[Union(10, typeof(ChatPluginChatContextDisplayBlock))]
[Union(11, typeof(ChatPluginTerminalDisplayBlock))]
public abstract partial class ChatPluginDisplayBlock : ObservableObject
{
    /// <summary>
    /// Indicates whether this display block is waiting for user input.
    /// </summary>
    [IgnoreMember]
    public virtual bool IsWaitingForUserInput => false;
}

/// <summary>
/// Represents a container block that can hold other display blocks.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginContainerDisplayBlock : ChatPluginDisplayBlock, IEnumerable<ChatPluginDisplayBlock>, IDisposable
{
    /// <summary>
    /// Gets the child display blocks of this container for MVVM binding.
    /// WARNING that this collection is not strongly synchronized, but it is observed on the UI dispatcher.
    /// </summary>
    [IgnoreMember]
    public ReadOnlyObservableCollection<ChatPluginDisplayBlock> Children { get; }

    [IgnoreMember]
    public IChatPluginDisplaySink DisplaySink => _displaySink;

    [Key(0)] private readonly ChatPluginDisplaySink _displaySink;
    [IgnoreMember] private readonly IDisposable _displaySinkConnection;

    [SerializationConstructor]
    private ChatPluginContainerDisplayBlock(ChatPluginDisplaySink displaySink)
    {
        _displaySink = displaySink;
        Children = _displaySink
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(out _displaySinkConnection);
    }

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
        _displaySink.Dispose();
        _displaySinkConnection.Dispose();
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
public sealed partial class ChatPluginDynamicResourceKeyDisplayBlock(IDynamicResourceKey key, string? className = null) : ChatPluginDisplayBlock
{
    [Key(0)]
    public IDynamicResourceKey Key { get; } = key;

    [Key(1)]
    public string? ClassName { get; } = className;
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginMarkdownDisplayBlock : ChatPluginDisplayBlock
{
    public ThreadSafeObservableStringBuilder MarkdownBuilder { get; } = new();

    [Key(0)]
    private string Markdown
    {
        get => MarkdownBuilder.ToString();
        set => MarkdownBuilder.Clear().Append(value);
    }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginProgressDisplayBlock(IDynamicResourceKey headerKey) : ChatPluginDisplayBlock
{
    [field: AllowNull, MaybeNull]
    public Progress<double> ProgressReporter => field ??= new Progress<double>(value => Progress = value);

    [Key(0)]
    public IDynamicResourceKey HeaderKey { get; } = headerKey;

    [Key(1)]
    [ObservableProperty]
    public partial double Progress { get; set; }
}

/// <summary>
/// Represents a reference to a file or folder in a chat plugin display block.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public partial class ChatPluginFileReference(
    string fullPath,
    IDynamicResourceKey? displayNameKey = null,
    IReadOnlySet<ChatPluginFileReference.Location>? locations = null
)
{
    [Key(0)]
    public string FullPath { get; } = fullPath;

    [Key(1)]
    public IDynamicResourceKey? DisplayNameKey { get; } = displayNameKey;

    [Key(2)]
    public IReadOnlySet<Location>? Locations { get; } = locations;

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

    public readonly record struct Location(int Line, int Column);
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginFileReferencesDisplayBlock(params IReadOnlyList<ChatPluginFileReference> references) : ChatPluginDisplayBlock
{
    [Key(0)]
    public IReadOnlyList<ChatPluginFileReference> References { get; } = references.AsValueEnumerable().Take(10).ToList();

    [Key(1)]
    public int TotalReferenceCount { get; set; } = references.Count;

    [IgnoreMember]
    public bool HasMoreReferences => TotalReferenceCount > References.Count;
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
[method: SerializationConstructor]
public sealed partial class ChatPluginFileDifferenceDisplayBlock(TextDifference difference) : ChatPluginDisplayBlock
{
    [Key(0)]
    public TextDifference Difference { get; } = difference;

    public string? OriginalText { get; init; }

    public override bool IsWaitingForUserInput => Difference.Acceptance is null;

    public ChatPluginFileDifferenceDisplayBlock(TextDifference difference, string originalText) : this(difference)
    {
        OriginalText = originalText;

        // Only subscribe to property changes in this constructor since deserialization will not change the Difference property.
        difference.PropertyChanged += HandleDifferencePropertyChanged;
    }

    /// <summary>
    /// Handles property changes on the TextDifference to update the IsWaitingForUserInput property.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void HandleDifferencePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TextDifference.Acceptance)) OnPropertyChanged(nameof(IsWaitingForUserInput));
    }
}

/// <summary>
/// Represents a URL with an optional display name key. Usage example: web search results.
/// </summary>
/// <param name="url"></param>
/// <param name="displayNameKey"></param>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginUrl(string? url, IDynamicResourceKey displayNameKey)
{
    [Key(0)]
    public string? Url { get; } = url;

    [Key(1)]
    public IDynamicResourceKey DisplayNameKey { get; } = displayNameKey;

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

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatPluginChatContextDisplayBlock(ChatContext chatContext) : ChatPluginDisplayBlock
{
    [Key(0)]
    public ChatContext ChatContext { get; } = chatContext;
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
    [ObservableProperty]
    public partial int? ExitCode { get; private set; }

    [Key(4)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Elapsed))]
    public partial DateTimeOffset? FinishedAt { get; private set; }

    [IgnoreMember]
    public TimeSpan Elapsed => (FinishedAt ?? DateTimeOffset.UtcNow) - CreatedAt;

    [IgnoreMember] private TerminalSession? _session;

    public ChatPluginTerminalDisplayBlock(
        ShellType shellType,
        TerminalRun run,
        TerminalSession session)
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
