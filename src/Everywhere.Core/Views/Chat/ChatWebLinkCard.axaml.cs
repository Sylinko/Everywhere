using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Everywhere.Chat.Links;

namespace Everywhere.Views;

/// <summary>
/// Renders one preview for an active chat link. Images are decoded by the bounded preview service.
/// </summary>
public sealed partial class ChatWebLinkCard : UserControl, IDisposable
{
    private WebPreview? _shown;
    private byte[]? _iconBytes;
    private Bitmap? _icon;
    private Bitmap? _cover;

    public ChatWebLinkCard()
    {
        InitializeComponent();
        PreviewPanel.TargetContent = PreviewPresenter.Content;
    }

    internal void SetAddress(Uri address, double availableWidth)
    {
        // Size a hostname-only card to the text. Metadata can expand both axes up to the available width.
        PreviewPresenter.MaxWidth = Math.Clamp(availableWidth - 32, 0, 406);
        PreviewPresenter.MinWidth = Math.Min(132, PreviewPresenter.MaxWidth);
        AddressText.Text = address.IdnHost;
    }

    internal void Show(WebPreview preview)
    {
        TitleText.Text = preview.Title;
        TitleText.IsVisible = preview.Title is not null;
        SummaryText.Text = preview.Description;
        SummaryText.IsVisible = preview.Description is not null;
        TextDetails.IsVisible = TitleText.IsVisible || SummaryText.IsVisible;

        if (preview.Icon is { } icon) SetIcon(icon);

        if (!ReferenceEquals(_shown?.Cover, preview.Cover))
        {
            _cover = SetBitmap(CoverImage, _cover, preview.Cover);
        }

        Details.IsVisible = TextDetails.IsVisible || _cover is not null;
        _shown = preview;
    }

    internal void SetIcon(byte[]? bytes)
    {
        if (ReferenceEquals(_iconBytes, bytes)) return;
        _icon = SetBitmap(SiteIcon, _icon, bytes);
        _iconBytes = bytes;
        DefaultIcon.IsVisible = _icon is null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cover = SetBitmap(CoverImage, _cover, null);
        SetIcon(null);
        _shown = null;
    }

    private static Bitmap? SetBitmap(Image image, Bitmap? previous, byte[]? bytes)
    {
        image.Source = null;
        previous?.Dispose();
        if (bytes is null)
        {
            image.IsVisible = false;
            return null;
        }

        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new Bitmap(stream);
        image.Source = bitmap;
        image.IsVisible = true;
        return bitmap;
    }
}
