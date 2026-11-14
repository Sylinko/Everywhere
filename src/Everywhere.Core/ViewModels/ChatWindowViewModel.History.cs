using Avalonia.Controls;
using Avalonia.Xaml.Interactions.Custom;
using Avalonia.Xaml.Interactivity;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Chat;
using Everywhere.Cloud;
using Everywhere.Messages;
using Everywhere.Views;
using Everywhere.Views.Pages;
using ShadUI;

namespace Everywhere.ViewModels;

partial class ChatWindowViewModel
{
    internal IChatSyncService? SyncService { get; }

    /// <summary>
    /// Whether cloud features are registered for this application build.
    /// </summary>
    public bool HasSyncService => SyncService is not null;

    [RelayCommand]
    private void OpenChatSyncManagement()
    {
        WeakReferenceMessenger.Default.Send<ApplicationMessage>(
            new ShowWindowMessage(ShowWindowMessage.MainWindow, typeof(ChatSyncPage)));
    }

    [RelayCommand]
    private async Task ManageChatSyncAsync(ChatMetadata metadata, CancellationToken cancellationToken)
    {
        if (SyncService is not { } sync)
        {
            return;
        }
        if (sync.Session is not { } session)
        {
            ToastHost.CreateToast((sync.AccountId is null ? LocaleKey.ChatSync_SignInRequired : LocaleKey.ChatSync_Disabled).I18N()).ShowInfo();
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Token, cancellationToken);
        var token = cancellation.Token;
        try
        {
            var decision = await session.ReadDecisionAsync(metadata.Id, token);
            token.ThrowIfCancellationRequested();
            if (decision?.Item is not { } item || item.IsBusy)
            {
                ToastHost.CreateToast(LocaleKey.ChatWindowViewModel_ChatSyncUnavailableToast_Content.I18N()).ShowInfo();
                return;
            }
            if (item.Participation == ChatSyncParticipation.Cloud)
            {
                if (await ChatSyncDialogs.ConfirmStopAsync(DialogHost, [item], token))
                {
                    await session.StopAsync(decision, Guid.NewGuid(), token);
                }
            }
            else if (item.CanStart && await ChatSyncDialogs.ConfirmStartAsync(DialogHost, item, token))
            {
                await session.StartAsync(decision, token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
            {
                var message = ChatSyncPresentation.DescribeError(error, LocaleKey.ChatSync_ActionUnconfirmed);
                ToastHost.CreateToast(message.ToString() ?? LocaleKey.ChatSync_ActionUnconfirmed.I18N()).ShowError();
            }
        }
    }

    internal void CancelSyncDialogs() => ManageChatSyncCommand.Cancel();

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

    private bool CanOrganizeChat(ChatMetadata metadata)
    {
        // Only the current draft can appear in history without any retained messages.
        var current = ChatContextManager.Current.Data;
        return !ReferenceEquals(current.Metadata, metadata) || current.HasHistory;
    }

    [RelayCommand(CanExecute = nameof(CanOrganizeChat))]
    private Task ToggleChatPinAsync(ChatMetadata metadata)
    {
        return ExecuteHistoryOperationAsync(() => ChatContextManager.SetPinnedAsync(metadata, !metadata.IsPinned));
    }

    [RelayCommand(CanExecute = nameof(CanOrganizeChat))]
    private async Task ArchiveChatAsync(ChatMetadata metadata)
    {
        if (ChatContextManager.HasRunningWork(metadata))
        {
            var result = await DialogHost
                .CreateDialog(LocaleKey.ChatWindowViewModel_ArchiveRunningChatDialog_Content.I18N(), LocaleKey.ChatWindowViewModel_ArchiveRunningChatDialog_Title.I18N())
                .WithPrimaryButton(LocaleKey.Common_OK.I18N())
                .WithCancelButton(LocaleKey.Common_Cancel.I18N())
                .ShowAsync();
            if (result != DialogResult.Primary)
            {
                return;
            }
        }
        await ExecuteHistoryOperationAsync(() => ChatContextManager.ArchiveAsync(metadata));
    }

    [RelayCommand]
    private Task RestoreChatAsync(ChatMetadata metadata)
    {
        return ExecuteHistoryOperationAsync(() => ChatContextManager.RestoreAsync(metadata));
    }

    [RelayCommand]
    private async Task SetTemporaryChatAsync(bool isTemporary)
    {
        var context = ChatContextManager.Current;
        if (isTemporary)
        {
            await ExecuteHistoryOperationAsync(() => context.ApplyAsync(() => context.Data.Metadata.SetTemporary(true)));
        }
        else
        {
            await PersistChatCommand.ExecuteAsync(context.Data.Metadata);
        }
    }

    [RelayCommand]
    private Task PersistChatAsync(ChatMetadata metadata)
    {
        return ExecuteHistoryOperationAsync(() => ChatContextManager.PersistChatAsync(metadata));
    }

    [RelayCommand]
    private async Task DeleteChatPermanentlyAsync(ChatMetadata metadata)
    {
        if (metadata.States.HasFlag(ChatContextMetadataStates.Busy))
        {
            ToastHost.CreateToast(LocaleKey.ChatHistory_Delete_Running.I18N()).ShowInfo();
            return;
        }

        var result = await DialogHost
            .CreateDialog(LocaleKey.ChatWindowViewModel_DeleteChatDialog_Content.I18N(), LocaleKey.ChatHistory_DeleteDialog_Title.I18N())
            .WithPrimaryButton(LocaleKey.Common_Delete.I18N(), buttonStyle: ButtonStyle.Destructive)
            .WithCancelButton(LocaleKey.Common_Cancel.I18N())
            .ShowAsync();
        if (result != DialogResult.Primary)
        {
            return;
        }

        await ExecuteHistoryOperationAsync(() => ChatContextManager.DeletePermanentlyAsync(metadata));
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