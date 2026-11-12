namespace Everywhere.Common.Notification;

/// <summary>
/// Identifies application windows that may present an announcement.
/// </summary>
[Flags]
public enum NotificationWindows
{
    Main = 1,
    Chat = 2,
    All = Main | Chat
}

/// <summary>
/// Defines whether acknowledgment survives an application restart.
/// </summary>
public enum NotificationRepeat
{
    Once,
    PerSession
}

/// <summary>
/// Describes an announcement whose rich-text visual tree is created only when a window can present it.
/// Local callers may supply an existing View; remote sources supply only declarative content.
/// </summary>
public sealed record PopupNotification
{
    /// <summary>
    /// Gets the stable identity within the publishing source.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the dynamically localized title.
    /// </summary>
    public required IDynamicLocaleKey TitleKey { get; init; }

    /// <summary>
    /// Gets a local Control or a declarative NotificationContent rendered through typed templates.
    /// </summary>
    public required object Content { get; init; }

    /// <summary>
    /// Gets the acknowledgment button text.
    /// </summary>
    public IDynamicLocaleKey AcknowledgeKey { get; init; } = new DynamicLocaleKey(LocaleKey.Common_OK);

    /// <summary>
    /// Gets the eligible window roles.
    /// </summary>
    public NotificationWindows Windows { get; init; } = NotificationWindows.All;

    /// <summary>
    /// Gets the acknowledgment lifetime.
    /// </summary>
    public NotificationRepeat Repeat { get; init; } = NotificationRepeat.Once;

    /// <summary>
    /// Gets the UTC deadline after which presentation must not start.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}