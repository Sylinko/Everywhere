using Everywhere.Collections;
using Everywhere.Common;

namespace Everywhere.Chat;

/// <summary>
/// Presents one stable section from the incremental chat history result.
/// </summary>
public sealed class ChatHistorySection : IDisposable
{
    /// <summary>
    /// Gets the section identity and display order.
    /// </summary>
    public ChatHistorySectionKind Kind { get; }

    /// <summary>
    /// Gets the localized section heading.
    /// </summary>
    public IDynamicLocaleKey HeaderKey { get; }

    /// <summary>
    /// Gets the section's metadata ordered from most recently modified to least recently modified.
    /// </summary>
    public IReadOnlyBindableList<ChatMetadata> Items { get; }

    private readonly IDisposable _metadataConnection;

    internal ChatHistorySection(IGroup<ChatMetadata, ChatHistorySectionKind> group)
    {
        Kind = group.GroupKey;
        HeaderKey = new DynamicLocaleKey(
            Kind switch
            {
                ChatHistorySectionKind.Pinned => LocaleKey.ChatHistory_Pinned,
                ChatHistorySectionKind.Today => LocaleKey.HumanizedDate_Today,
                ChatHistorySectionKind.Yesterday => LocaleKey.HumanizedDate_Yesterday,
                ChatHistorySectionKind.LastWeek => LocaleKey.HumanizedDate_LastWeek,
                ChatHistorySectionKind.LastMonth => LocaleKey.HumanizedDate_LastMonth,
                ChatHistorySectionKind.LastYear => LocaleKey.HumanizedDate_LastYear,
                _ => LocaleKey.HumanizedDate_Earlier
            });
        Items = group.List
            .Connect()
            .Sort(
                SortExpressionComparer<ChatMetadata>
                    .Descending(static metadata => metadata.DateModified)
                    .ThenByDescending(static metadata => metadata.Id))
            .ObserveOnAvaloniaDispatcher()
            .BindEx(out _metadataConnection);
    }

    /// <summary>
    /// Releases the DynamicData binding owned by this history section.
    /// </summary>
    public void Dispose() => _metadataConnection.Dispose();
}

/// <summary>
/// Presentation-only history sections; pinning is independent of date buckets and future classification.
/// </summary>
public enum ChatHistorySectionKind
{
    Pinned,
    Today,
    Yesterday,
    LastWeek,
    LastMonth,
    LastYear,
    Earlier
}