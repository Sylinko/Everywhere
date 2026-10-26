using System.ComponentModel;
using Avalonia.Threading;
using Everywhere.Chat;
using LiveMarkdown.Avalonia;

namespace Everywhere.Views;

/// <summary>
/// Bridges an explicitly expanded standalone display block to parsed Markdown. It also works in
/// consent/review views without a ChatTurn. Detaching releases observation and its document cache.
/// </summary>
public sealed class ChatMarkdownRenderer : MarkdownRenderer
{
    /// <summary>
    /// Defines the single model text source.
    /// </summary>
    public static readonly StyledProperty<ChatTextBuffer?> SourceProperty =
        AvaloniaProperty.Register<ChatMarkdownRenderer, ChatTextBuffer?>(nameof(Source));

    /// <summary>
    /// Gets or sets the source to render while attached.
    /// </summary>
    public ChatTextBuffer? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(MarkdownRenderer);

    private ChatTextBuffer? _observedSource;
    private ChatMarkdownPresentation? _presentation;
    private bool _isAttached;
    private int _isRefreshPosted;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        Reconnect();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        Disconnect();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty && _isAttached)
        {
            Reconnect();
        }
    }

    private void Reconnect()
    {
        Disconnect();
        if (Source is not { } source)
        {
            return;
        }

        _observedSource = source;
        _presentation = new ChatMarkdownPresentation(source);
        _presentation.PropertyChanged += HandlePresentationChanged;
        source.Changed += HandleSourceChanged;
        _presentation.Refresh();
    }

    private void Disconnect()
    {
        if (_observedSource is not null)
        {
            _observedSource.Changed -= HandleSourceChanged;
            _observedSource = null;
        }

        if (_presentation is not null)
        {
            _presentation.PropertyChanged -= HandlePresentationChanged;
            _presentation.Dispose();
            _presentation = null;
        }

        DocumentUpdate = null;
    }

    private void HandleSourceChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _isRefreshPosted, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _isRefreshPosted, 0);
            _presentation?.Refresh();
        });
    }

    private void HandlePresentationChanged(object? sender, PropertyChangedEventArgs e)
    {
        DocumentUpdate = _presentation?.DocumentUpdate;
    }
}