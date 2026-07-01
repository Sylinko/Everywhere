using LiveMarkdown.Avalonia;

namespace Everywhere.Views;

public sealed partial class ChangeLogView : ReactiveUserControl<ChangeLogViewModel>
{
    public ChangeLogView(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        InitializeComponent();
    }

    private void HandleMarkdownRendererLinkClick(object? sender, LinkClickedEventArgs e)
    {
        if (e.HRef is not { IsAbsoluteUri: true, Scheme: "https" or "http" } href) return;

        App.Launcher.LaunchUriAsync(href);
    }
}