using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Everywhere.Chat;
using Markdig;

namespace Everywhere.Views;

/// <summary>
/// A bounded preview with a font-relative minimum size and one invalidation subscription to its realized turn.
/// </summary>
public sealed partial class ChatTurnPreview : UserControl
{
    /// <summary>
    /// Defines the indexed turn displayed by this preview.
    /// </summary>
    public static readonly StyledProperty<ChatTurnNavigationIndex.Entry?> EntryProperty =
        AvaloniaProperty.Register<ChatTurnPreview, ChatTurnNavigationIndex.Entry?>(nameof(Entry));

    /// <summary>
    /// Gets or sets the indexed turn displayed by this preview.
    /// </summary>
    public ChatTurnNavigationIndex.Entry? Entry
    {
        get => GetValue(EntryProperty);
        set => SetValue(EntryProperty, value);
    }

    /// <summary>
    /// Defines the response line budget used for the overall minimum size.
    /// </summary>
    public static readonly StyledProperty<double> ResponseLineCountProperty =
        AvaloniaProperty.Register<ChatTurnPreview, double>(nameof(ResponseLineCount));

    /// <summary>
    /// Gets the response line budget, independent of streamed text wrapping.
    /// </summary>
    public double ResponseLineCount
    {
        get => GetValue(ResponseLineCountProperty);
        private set => SetValue(ResponseLineCountProperty, value);
    }

    private string? _questionSource;
    private string? _answerSource;
    private ChatTurn? _turn;
    private ChatTurnNavigationIndex? _index;
    private ChatTurnNavigationIndex.Entry? _entry;
    private int _isRefreshQueued;
    private CancellationTokenSource? _loadCancellation;

    /// <summary>
    /// Creates the preview visual tree.
    /// </summary>
    public ChatTurnPreview() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveEntry();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Release();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EntryProperty || VisualRoot is null) return;

        Release();
        ObserveEntry();
    }

    /// <summary>
    /// Observes the native turn. Compression and continuation keep the same preview identity.
    /// </summary>
    private void ObserveEntry()
    {
        if (Entry is not { } entry) return;
        _index = entry.Index;
        _entry = entry;
        _turn = entry.Turn;
        if (_turn is not null)
        {
            _turn.Invalidated += HandleTurnInvalidated;
        }

        _index.Changed += HandleIndexChanged;
        LoadPreviewAsync(entry).Detach();
    }

    private async Task LoadPreviewAsync(ChatTurnNavigationIndex.Entry entry)
    {
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;
        try
        {
            await entry.Index.Context.EnsureTurnLoadedAsync(entry.Turn, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && ReferenceEquals(_entry, entry))
            {
                Refresh();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Stops observing the current turn so this preview can be reused for another realized entry.
    /// </summary>
    private void Release()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _questionSource = null;
        _answerSource = null;
        Question.Text = null;
        Answer.Text = null;
        Answer.IsVisible = false;
        StatusRegion.IsVisible = false;
        ResponseLineCount = 0;
        if (_index is not null) _index.Changed -= HandleIndexChanged;
        if (_turn is not null)
        {
            _turn.Invalidated -= HandleTurnInvalidated;
            _turn = null;
        }
        _index = null;
        _entry = null;
    }

    private void HandleIndexChanged() => Refresh();

    private void Refresh()
    {
        if (_entry is not { } entry) return;
        var nodes = entry.Turn.SelectedNodes;
        if (nodes.Any(node => !node.IsMessageLoaded)) return;

        var messages = nodes.Select(node => node.Message).ToList();
        var assistants = messages.OfType<AssistantChatMessage>().ToList();
        var question = entry.Node.Message switch
        {
            UserChatMessage user when !string.IsNullOrWhiteSpace(user.Content) => user.Content,
            UserChatMessage user => new StringBuilder().AppendJoin(" · ", user.Attachments.Select(attachment => attachment.HeaderKey.ToString()))
                .ToString(),
            UserActionChatMessage action => action.HeaderKey?.ToString() ?? action.Content ?? string.Empty,
            _ => string.Empty
        };
        question = question.TruncateUtf16(512);
        var answer = new StringBuilder();
        foreach (var message in assistants)
        {
            // Limit preview allocation independently of the size of the retained turn.
            foreach (var span in message.Items.OfType<AssistantChatMessageTextSpan>())
            {
                var remaining = 768 - answer.Length - (answer.Length > 0 ? 1 : 0);
                if (remaining <= 0) break;
                var text = span.ContentMarkdownBuilder.GetPrefix(remaining);
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (answer.Length > 0) answer.Append(' ');
                answer.Append(text);
                if (answer.Length >= 768) break;
            }
            if (answer.Length >= 768) break;
        }

        var answerSource = answer.ToString();
        if (_questionSource != question)
        {
            _questionSource = question;
            Question.Text = Markdown.ToPlainText(question).Trim();
        }
        if (_answerSource != answerSource)
        {
            _answerSource = answerSource;
            Answer.Text = Markdown.ToPlainText(answerSource).Trim();
        }

        Answer.IsVisible = !string.IsNullOrWhiteSpace(Answer.Text);
        var (status, isError) = GetStatus(assistants.LastOrDefault(), Answer.IsVisible);
        if (assistants.Count == 0 && messages.AsValueEnumerable().OfType<ActionChatMessage>().LastOrDefault() is { } pendingAction)
        {
            status = pendingAction.ErrorMessageKey?.ToString() ?? pendingAction.HeaderKey?.ToString();
            isError = pendingAction.ErrorMessageKey is not null;
        }

        Status.Text = status;
        StatusRegion.IsVisible = !string.IsNullOrWhiteSpace(status);
        StatusRegion.Classes.Set("error", isError);
        ResponseLineCount = (Answer.IsVisible ? 3 : 0) + (StatusRegion.IsVisible ? 2 : 0);
    }

    private static (string? Text, bool IsError) GetStatus(AssistantChatMessage? assistant, bool hasAnswer)
    {
        if (assistant is null) return (null, false);
        if (assistant.ErrorMessageKey is { } error) return (error.ToString().TruncateUtf16(512), true);
        var lastSpan = assistant.Items
            .AsValueEnumerable()
            .LastOrDefault(span => span is not AssistantChatMessageTextSpan text || text.ContentMarkdownBuilder.Length > 0);
        if (lastSpan is AssistantChatMessageFunctionCallSpan functions)
        {
            var call = functions.Items.AsValueEnumerable().LastOrDefault();
            if (call?.ErrorMessageKey is { } callError) return (callError.ToString().TruncateUtf16(512), true);
            var header = call?.HeaderKey?.ToString() ?? LocaleKey.ChatTurnPreview_ToolActivity.I18N();
            return (FormatActivityStatus(header, assistant.IsBusy), false);
        }

        if (lastSpan is AssistantChatMessageReasoningSpan)
        {
            return (FormatActivityStatus(LocaleKey.ChatMessageControl_Assistant_Reasoning.I18N(), assistant.IsBusy), false);
        }
        if (lastSpan is AssistantChatMessageImageSpan)
            return (LocaleKey.Modalities_Image.I18N(), false);
        if (lastSpan is AssistantChatMessageTextSpan && hasAnswer) return (null, false);
        return (
            assistant.IsBusy ? LocaleKey.ChatTurnPreview_Waiting.I18N() : LocaleKey.ChatPresentationRowPresenter_NoResponse.I18N(),
            false);
    }

    private static string FormatActivityStatus(string header, bool isBusy) => isBusy ?
        header :
        new StringBuilder(header).Append('\n').Append(LocaleKey.ChatPresentationRowPresenter_NoResponse.I18N()).ToString();

    private void HandleTurnInvalidated(object? sender, EventArgs e)
    {
        // Clear the ingress flag before capture so a concurrent append schedules another pass.
        if (Interlocked.Exchange(ref _isRefreshQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _isRefreshQueued, 0);
            Refresh();
        }, DispatcherPriority.Background);
    }
}
