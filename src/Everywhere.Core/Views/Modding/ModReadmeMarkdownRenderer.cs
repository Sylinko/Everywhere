using LiveMarkdown.Avalonia;
using Markdig;

namespace Everywhere.Views.Modding;

/// <summary>
/// Renders third-party mod README Markdown with web link previews while blocking HTML and local filesystem links.
/// </summary>
public sealed class ModReadmeMarkdownRenderer : ChatLinkRenderer
{
    /// <summary>
    /// Defines the Markdown document text displayed by this renderer.
    /// </summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ModReadmeMarkdownRenderer, string?>(nameof(Text));

    /// <summary>
    /// Gets or sets the Markdown document text.
    /// </summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static readonly MarkdownPipeline ReadmePipeline = NotificationMarkdownRenderer.CreateRemoteSafePipeline();

    /// <summary>
    /// Initializes a new README renderer restricted to remote HTTPS images and web links.
    /// </summary>
    public ModReadmeMarkdownRenderer()
    {
        AllowLocalLinks = false;
        UpdateProducer = new MarkdownUpdateProducer { Pipeline = ReadmePipeline };
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            MarkdownBuilder = string.IsNullOrEmpty(Text) ? null : new ObservableStringBuilder(Text);
        }
    }
}
