using Avalonia.Controls.Notifications;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Windows.Interop;

namespace Everywhere.Windows.Initialization;

/// <summary>Warns when the interactive application is running with an administrative token.</summary>
internal sealed class ElevatedMainNotificationInitializer(
    INotificationPublisher<ElevatedMainNotificationInitializer> notificationPublisher
) : IAsyncInitializer
{
    private const string NotificationId = "elevated_main";

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ProcessSecurity.HasAdministrativeToken)
        {
            notificationPublisher.Push(
                NotificationId,
                new DynamicLocaleKey(Core.I18N.LocaleKey.Windows_ElevatedMainWarning_Content),
                NotificationType.Warning,
                canDismiss: true,
                forceShow: true);
        }

        return Task.CompletedTask;
    }
}