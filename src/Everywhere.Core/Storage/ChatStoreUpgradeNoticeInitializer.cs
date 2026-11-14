using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Views;
using Microsoft.Extensions.Logging;

namespace Everywhere.Storage;

/// <summary>
/// Registers the completed local import notice without opening a window or blocking startup on
/// user input. The existing store acknowledgment remains authoritative for this database;
/// the notification center only deduplicates this local notice within the current process.
/// </summary>
public sealed class ChatStoreUpgradeNoticeInitializer(
    ChatStoreUpgradeService upgrade,
    NotificationCenter notificationCenter,
    ILogger<ChatStoreUpgradeNoticeInitializer> logger) : IAsyncInitializer
{
    /// <inheritdoc />
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!await upgrade.NeedsNoticeAsync(cancellationToken)) return;
        ShowNoticeAsync(cancellationToken).Detach(logger.ToExceptionHandler());
    }

    private async Task ShowNoticeAsync(CancellationToken cancellationToken)
    {
        var view = new ChatUpgradeNoticeView();
        var notification = new PopupNotification
        {
            Id = "chat-store-v2",
            TitleKey = new DynamicLocaleKey(LocaleKey.ChatUpgrade_Title),
            AcknowledgeKey = new DynamicLocaleKey(LocaleKey.ChatUpgrade_Continue),
            Repeat = NotificationRepeat.PerSession,
            Content = view
        };
        if (!await notificationCenter.ShowPopupAsync("local", notification, cancellationToken)) return;

        await upgrade.AcknowledgeAsync(cancellationToken);
        if (!view.ShouldTrashLegacyDatabase) return;

        try
        {
            await upgrade.TrashLegacyDatabaseAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Canceling native trash is best-effort; it does not undo acknowledgment.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not move all previous-version chat database files to the trash.");
        }
    }
}