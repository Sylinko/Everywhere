using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Common.Notification;

namespace Everywhere.Chat;

public interface IChatWindowNotificationService
{
    IReadOnlyBindableList<DynamicNotification> Notifications { get; }
}
