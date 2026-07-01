using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;

namespace Everywhere.Views;

public partial class ChatPluginDisplayBlockPresenter : ContentControl
{
    [RelayCommand]
    public async Task OpenUrlAsync(string url)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;
        await topLevel.Launcher.LaunchUriAsync(new Uri(url));
    }
}