using System.ComponentModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Everywhere.Chat;
using Markdig;

namespace Everywhere.Views;

/// <summary>
/// A bounded preview with a font-relative minimum size and subscriptions only to running assistants.
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
    private readonly HashSet<AssistantChatMessage> _runningAssistants = [];
    private ChatTurnNavigationIndex? _index;
    private ChatTurnNavigationIndex.Entry? _entry;
    private bool _isRefreshQueued;

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
    /// Reads the turn and observes its active assistants. Branch updates can add another assistant
    /// after compression without replacing the preview or subscriptions to unchanged messages.
    /// </summary>
    private void ObserveEntry()
    {
        if (Entry is not { } entry) return;
        _index = entry.Index;
        _entry = entry;
        _index.Changed += HandleIndexChanged;
        Refresh();
    }

    /// <summary>
    /// Stops observing the current turn so this preview can be reused for another realized entry.
    /// </summary>
    private void Release()
    {
        if (_index is not null) _index.Changed -= HandleIndexChanged;
        foreach (var assistant in _runningAssistants) assistant.PropertyChanged -= HandleAssistantChanged;
        _runningAssistants.Clear();
        _index = null;
        _entry = null;
    }

    private void HandleIndexChanged() => Refresh();

    private void Refresh()
    {
        if (_index is not { } index || _entry is not { } entry) return;
        var messages = index.GetMessages(entry).ToList();
        var assistants = messages.OfType<AssistantChatMessage>().ToList();
        foreach (var assistant in _runningAssistants.ToArray())
        {
            if (assistant.IsBusy && assistants.Contains(assistant)) continue;
            assistant.PropertyChanged -= HandleAssistantChanged;
            _runningAssistants.Remove(assistant);
        }
        foreach (var assistant in assistants)
        {
            if (assistant.IsBusy && _runningAssistants.Add(assistant))
                assistant.PropertyChanged += HandleAssistantChanged;
        }

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
            // Read the model snapshot, not the asynchronously bound Spans collection. Completion
            // can precede UI delivery of its final additions, but the source is already complete.
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
            var header = call?.HeaderKey?.ToString() ?? LocaleResolver.ChatTurnPreview_ToolActivity;
            return (FormatActivityStatus(header, assistant.IsBusy), false);
        }

        if (lastSpan is AssistantChatMessageReasoningSpan)
        {
            return (FormatActivityStatus(LocaleResolver.ChatMessageControl_Assistant_Reasoning, assistant.IsBusy), false);
        }
        if (lastSpan is AssistantChatMessageImageSpan)
            return (LocaleResolver.Modalities_Image, false);
        if (lastSpan is AssistantChatMessageTextSpan && hasAnswer) return (null, false);
        return (
            assistant.IsBusy ? LocaleResolver.ChatTurnPreview_Waiting : LocaleResolver.ChatPresentationRowPresenter_NoResponse,
            false);
    }

    private static string FormatActivityStatus(string header, bool isBusy) => isBusy ?
        header :
        new StringBuilder(header).Append('\n').Append(LocaleResolver.ChatPresentationRowPresenter_NoResponse).ToString();

    private void HandleAssistantChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not
            nameof(AssistantChatMessage.Spans) and not
            nameof(AssistantChatMessage.IsBusy) and not
            nameof(AssistantChatMessage.ErrorMessageKey)) return;

        // This is the sole worker-to-UI boundary. Coalesce notifications within a dispatcher turn,
        // with no timer, delayed completion, or per-span subscription graph.
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isRefreshQueued) return;

            _isRefreshQueued = true;
            Dispatcher.UIThread.Post(
                () =>
                {
                    _isRefreshQueued = false;
                    Refresh();
                },
                DispatcherPriority.Background);
        });
    }
}
