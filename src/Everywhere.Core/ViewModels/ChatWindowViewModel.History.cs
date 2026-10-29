using Avalonia.Controls;
using Avalonia.Xaml.Interactions.Custom;
using Avalonia.Xaml.Interactivity;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat;
using ShadUI;

namespace Everywhere.ViewModels;

partial class ChatWindowViewModel
{
    [RelayCommand]
    private async Task RenameChatAsync(ChatMetadata metadata)
    {
        var textBox = new TextBox
        {
            Text = metadata.Topic,
            PlaceholderText = LocaleKey.ChatContext_Metadata_Topic_Default.I18N(), // no need to bind
            [ControlAssist.LabelProperty] = LocaleKey.ChatHistory_RenameDialog_Label.I18N(),
            [Interaction.BehaviorsProperty] = new BehaviorCollection
            {
                new AutoFocusBehavior(),
                new TextBoxSelectAllOnGotFocusBehavior()
            },
            MaxLength = 100,
            MinWidth = 280
        };

        var result = await DialogHost
            .CreateDialog(textBox, LocaleKey.ChatHistory_RenameDialog_Title.I18N())
            .WithPrimaryButton(LocaleKey.Common_OK.I18N())
            .WithCancelButton(LocaleKey.Common_Cancel.I18N())
            .ShowAsync();
        if (result != DialogResult.Primary)
        {
            return;
        }

        await ExecuteHistoryOperationAsync(() => ChatContextManager.RenameAsync(metadata, textBox.Text));
    }

    [RelayCommand]
    private Task ArchiveChatAsync(ChatMetadata metadata)
    {
        return ExecuteHistoryOperationAsync(() => ChatContextManager.ArchiveAsync(metadata));
    }

    [RelayCommand]
    private Task RestoreChatAsync(ChatMetadata metadata)
    {
        return ExecuteHistoryOperationAsync(() => ChatContextManager.RestoreAsync(metadata));
    }

    [RelayCommand]
    private Task DismissTemporaryChatAsync(ChatMetadata metadata)
    {
        return ExecuteHistoryOperationAsync(() => ChatContextManager.DismissTemporaryAsync(metadata));
    }

    [RelayCommand]
    private async Task DeleteArchivedChatAsync(ChatMetadata metadata)
    {
        if (!metadata.IsArchived)
        {
            return;
        }

        if (metadata.States.HasFlag(ChatContextMetadataStates.Busy))
        {
            ToastHost.CreateToast(LocaleKey.ChatHistory_Delete_Running.I18N()).ShowInfo();
            return;
        }

        var result = await DialogHost.CreateDialog(
                LocaleKey.ChatHistory_Delete_Confirmation.I18N(),
                LocaleKey.ChatHistory_Delete_Title.I18N())
            .WithPrimaryButton(LocaleKey.Common_Delete.I18N(), buttonStyle: ButtonStyle.Destructive)
            .WithCancelButton(LocaleKey.Common_Cancel.I18N())
            .ShowAsync();
        if (result == DialogResult.Primary)
        {
            await ExecuteHistoryOperationAsync(() => ChatContextManager.DeletePermanentlyAsync(metadata));
        }
    }

    private async Task ExecuteHistoryOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            ToastExceptionHandler.HandleException(exception);
        }
    }
}