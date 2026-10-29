using Everywhere.Chat;

namespace Everywhere.Storage;

/// <summary>
/// Captures the database sort values at a page boundary, independently of later metadata changes or deletion.
/// </summary>
public sealed record ChatHistoryCursor(bool IsPinned, DateTimeOffset UpdatedAt, Guid Id);

/// <summary>
/// A detached, pinned-first metadata page. A null continuation marks the end of the listing.
/// </summary>
public sealed record ChatHistoryPage(IReadOnlyList<ChatMetadata> Items, ChatHistoryCursor? NextCursor);