using System.ComponentModel;

namespace Everywhere.Cloud;

/// <summary>
/// Owns the authenticated device preference and its active synchronization session.
/// Chat commands and inventory belong to that session, not to a management page.
/// </summary>
public interface IChatSyncService : INotifyPropertyChanged
{
    /// <summary>
    /// Authenticated account subject, including when sync is disabled.
    /// </summary>
    string? AccountId { get; }

    /// <summary>
    /// Display identity for the account-specific device switch.
    /// </summary>
    string? AccountLabel { get; }

    /// <summary>
    /// Device preference. Enabling includes eligible history but preserves explicit exclusions.
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Active account session, or null when signed out, disabled or unconfigured.
    /// </summary>
    ChatSyncSession? Session { get; }

    /// <summary>
    /// Account, preference or current session state changed. May be raised off the UI thread.
    /// Observers read Session and its latest State rather than retaining an event payload.
    /// </summary>
    event EventHandler? Changed;
}