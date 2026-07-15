using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;

namespace Everywhere.Views;

public partial class ChatPluginDisplayBlockPresenter : ContentControl
{
    /// <summary>
    /// Opens a URL from a detailed display block. This presenter is also used by confirmation UI
    /// outside <see cref="ChatMessageItemsControl"/>, so it cannot depend on the chat-root command.
    /// </summary>
    [RelayCommand]
    private static Task<bool> OpenUrlAsync(string url) => App.Launcher.LaunchUriAsync(new Uri(url));
}