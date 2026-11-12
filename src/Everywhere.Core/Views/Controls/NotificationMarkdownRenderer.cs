using LiveMarkdown.Avalonia;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Everywhere.Views;

/// <summary>
/// Bridges localized static text to the existing background Markdown parser. No chat-specific
/// streaming state or presentation lifetime is involved.
/// </summary>
public sealed class NotificationMarkdownRenderer : MarkdownRenderer
{
    /// <summary>
    /// Defines the localized Markdown text input.
    /// </summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<NotificationMarkdownRenderer, string?>(nameof(Text));

    private static readonly MarkdownPipeline NotificationPipeline = CreatePipeline();

    /// <summary>
    /// Creates a background parser with a remote-document resource policy.
    /// </summary>
    public NotificationMarkdownRenderer()
    {
        UpdateProducer = new MarkdownUpdateProducer { Pipeline = NotificationPipeline };
    }

    /// <summary>
    /// Gets or sets the complete Markdown document.
    /// </summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(MarkdownRenderer);

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml();
        builder.DocumentProcessed += document =>
        {
            // Markdown image sources otherwise support file:// and local paths. Announcements
            // may load HTTPS images, but cannot use that renderer capability to read local files.
            foreach (var image in document.Descendants<LinkInline>().Where(link => link.IsImage))
            {
                if (!Uri.TryCreate(image.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https") image.Url = null;
            }
        };
        return builder.Build();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) MarkdownBuilder = new ObservableStringBuilder(Text);
    }
}