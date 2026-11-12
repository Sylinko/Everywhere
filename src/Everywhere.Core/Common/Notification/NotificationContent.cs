namespace Everywhere.Common.Notification;

/// <summary>
/// Contains a dynamically localized, complete rich-text document.
/// </summary>
public abstract record NotificationContent(IDynamicLocaleKey TextKey);

/// <summary>
/// Contains Markdown rendered by the existing Markdown pipeline.
/// </summary>
public sealed record MarkdownNotificationContent(IDynamicLocaleKey TextKey) : NotificationContent(TextKey);

/// <summary>
/// Contains static HTML, not a browser document or executable application.
/// </summary>
public sealed record HtmlNotificationContent(IDynamicLocaleKey TextKey) : NotificationContent(TextKey);