using Avalonia.Controls;
using Avalonia.LogicalTree;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using ShadUI;

namespace Everywhere.Views;

/// <summary>
/// Presents only inexpensive summary shapes for the latest plugin display block. Heavy controls
/// such as terminals, code editors, diffs, and child chat lists are intentionally not created here.
/// </summary>
public partial class ChatActivityPreviewPresenter : ContentControl
{
    [RelayCommand]
    private async Task OpenUrlAsync(string? url)
    {
        if (url is null || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        await topLevel.Launcher.LaunchUriAsync(new Uri(url));
    }

    [RelayCommand]
    private void OpenSubagent(ChatPluginSubagentDisplayBlock? block)
    {
        if (block is null) return;

        var parentItemsControl = this.FindLogicalAncestorOfType<ChatMessageItemsControl>();
        ServiceLocator.Resolve<DialogManager>()
            .CreateCustomDialog(
                new ChatSubagentView
                {
                    ChatContext = block.ChatContext,
                    SupportedModalities = parentItemsControl?.SupportedModalities ?? Modalities.None,
                })
            .Dismissible()
            .Show();
    }
}