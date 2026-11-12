using Avalonia.Controls;
using Avalonia.Interactivity;
using Everywhere.Common;
using Everywhere.Interop;
using LiveMarkdown.Avalonia;
using TheArtOfDev.HtmlRenderer.Avalonia;
using TheArtOfDev.HtmlRenderer.Core.Entities;

namespace Everywhere.Views;

/// <summary>
/// Renders declarative remote content without exposing local commands.
/// </summary>
public sealed partial class NotificationContentView : UserControl
{
    /// <summary>
    /// Creates the rich-text templates.
    /// </summary>
    public NotificationContentView()
    {
        InitializeComponent();
    }

    private void HandleMarkdownLinkClick(object? sender, LinkClickedEventArgs e)
    {
        e.Handled = true;
        OpenLink(e.HRef);
    }

    private void HandleHtmlLinkClicked(object? sender, RoutedEventArgs args)
    {
        if (args is not HtmlRendererRoutedEventArgs<HtmlLinkClickedEventArgs> e) return;
        e.Event.Handled = true;
        if (Uri.TryCreate(e.Event.Link, UriKind.Absolute, out var uri)) OpenLink(uri);
    }

    private void HandleHtmlImageLoad(object? sender, RoutedEventArgs args)
    {
        if (args is not HtmlRendererRoutedEventArgs<HtmlImageLoadEventArgs> e) return;
        if (!Uri.TryCreate(e.Event.Src, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            e.Event.Callback();
        }
    }

    private void HandleHtmlStylesheetLoad(object? sender, RoutedEventArgs args)
    {
        if (args is not HtmlRendererRoutedEventArgs<HtmlStylesheetLoadEventArgs> e) return;
        // Remote documents use the application's theme and inline CSS, never local/external stylesheets.
        e.Event.SetStyleSheet = " ";
    }

    private static void OpenLink(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true, Scheme: "https" }) return;
        BetterBclLauncher.Shared.LaunchUriAsync(uri).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }
}