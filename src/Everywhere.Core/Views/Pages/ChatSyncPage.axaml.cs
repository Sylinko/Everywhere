using Avalonia.Controls;

namespace Everywhere.Views.Pages;

/// <summary>
/// Manages per-chat cloud participation and account storage outside the settings categories.
/// </summary>
public sealed partial class ChatSyncPage : ReactiveUserControl<ChatSyncPageViewModel>
{
    /// <summary>
    /// Creates the account management page, retaining its view model across navigation.
    /// </summary>
    public ChatSyncPage(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        InitializeComponent();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (e.WidthChanged)
        {
            Classes.Set("Narrow", e.NewSize.Width < 500);
        }
    }
}