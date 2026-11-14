using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LiveMarkdown.Avalonia;
using ShadUI;

namespace Everywhere.Views;

/// <summary>
/// Presents terminal update guidance with chat Markdown and a separately rendered download link.
/// </summary>
public sealed partial class TerminalUpdateGuidance : UserControl
{
    /// <summary>
    /// Gets the exact command offered by the code block's copy button.
    /// </summary>
    public const string UpdateCommand = "ew update";

    /// <summary>
    /// Gets the stable Markdown source populated by short incremental updates.
    /// </summary>
    public ObservableStringBuilder MarkdownBuilder { get; } = new();

    /// <summary>
    /// Defines whether the message is still being revealed.
    /// </summary>
    private static readonly DirectProperty<TerminalUpdateGuidance, bool> IsStreamingProperty =
        AvaloniaProperty.RegisterDirect<TerminalUpdateGuidance, bool>(nameof(IsStreaming), control => control.IsStreaming);

    /// <summary>
    /// Gets whether the message is still streaming. Partial commands cannot be copied.
    /// </summary>
    public bool IsStreaming => _isStreaming;

    private const int StreamChunkCount = 8;

    private readonly IDynamicLocaleKey _messageKey = new FormattedDynamicLocaleKey(
        "{0}\n\n```sh\n{1}\n```",
        new DynamicLocaleKey(LocaleKey.TerminalUpdate_Introduction),
        new DirectLocaleKey(UpdateCommand));
    private readonly DispatcherTimer _streamTimer;
    private IDisposable? _messageSubscription;
    private DialogHost? _dialogHost;
    private string _pendingMarkdown = string.Empty;
    private int[] _textElementOffsets = [];
    private int _nextTextElement;
    private int _streamChunkSize;
    private bool _isStreaming;

    /// <summary>
    /// Creates the chat-style update message.
    /// </summary>
    public TerminalUpdateGuidance()
    {
        _streamTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _streamTimer.Tick += HandleStreamTick;
        InitializeComponent();
    }

    /// <summary>
    /// Shows update guidance on the requesting window, with copying owned by the chat code block.
    /// </summary>
    public static Task<DialogResult> ShowAsync(TopLevel? topLevel, CancellationToken cancellationToken = default)
    {
        var host = DialogManager.ResolveHost(topLevel);
        if (host is null)
        {
            return Task.FromResult(DialogResult.HostNotFound);
        }

        return host.CreateCustomDialog(new TerminalUpdateGuidance()).ShowAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _dialogHost = DialogManager.ResolveHost(TopLevel.GetTopLevel(this));
        _dialogHost?.Classes.Add("ChatUpdateGuidance");
        _messageSubscription = _messageKey.Subscribe(value => StartStreaming(value?.ToString() ?? string.Empty));
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _messageSubscription?.Dispose();
        _messageSubscription = null;
        StopStreaming();
        _dialogHost?.Classes.Remove("ChatUpdateGuidance");
        _dialogHost = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void StartStreaming(string markdown)
    {
        _streamTimer.Stop();
        _pendingMarkdown = markdown;
        // Keep combining characters and surrogate pairs intact while revealing each chunk.
        _textElementOffsets = StringInfo.ParseCombiningCharacters(markdown);
        _streamChunkSize = Math.Max(1, (_textElementOffsets.Length + StreamChunkCount - 1) / StreamChunkCount);
        _nextTextElement = 0;
        MarkdownBuilder.Clear();
        SetAndRaise(IsStreamingProperty, ref _isStreaming, _textElementOffsets.Length > 0);
        if (!IsStreaming)
        {
            return;
        }

        // Eight real append operations finish in roughly 490 ms after the first immediate chunk.
        _streamTimer.Start();
        AppendNextChunk();
    }

    private void HandleStreamTick(object? sender, EventArgs e)
    {
        if (IsStreaming)
        {
            AppendNextChunk();
        }
    }

    private void AppendNextChunk()
    {
        var start = _textElementOffsets[_nextTextElement];
        _nextTextElement = Math.Min(_nextTextElement + _streamChunkSize, _textElementOffsets.Length);
        var end = _nextTextElement < _textElementOffsets.Length ?
            _textElementOffsets[_nextTextElement] :
            _pendingMarkdown.Length;
        MarkdownBuilder.Append(_pendingMarkdown[start..end]);
        if (_nextTextElement == _textElementOffsets.Length)
        {
            StopStreaming();
        }
    }

    private void StopStreaming()
    {
        _streamTimer.Stop();
        SetAndRaise(IsStreamingProperty, ref _isStreaming, false);
    }

    private void HandleCancelClick(object? sender, RoutedEventArgs e)
    {
        StopStreaming();
        _dialogHost?.Close(this);
    }
}