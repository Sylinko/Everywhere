namespace Everywhere.Cloud;

/// <summary>
/// Previews which installed copies can be stopped to admit the newest quota-blocked upload.
/// A plan never falls back to cloud-only copies or combines shortfalls from separate submissions.
/// </summary>
/// <param name="Target">Newest quota-blocked chat to retry after releasing space, or null for account overage.</param>
/// <param name="RequiredBytes">Additional cloud bytes needed by that one upload, or the account overage.</param>
/// <param name="Copies">Confirmed installed cloud copies proposed for stopping, oldest-modified first.</param>
public sealed record ChatSyncSpacePlan(Guid? Target, long RequiredBytes, IReadOnlyList<ChatSyncItem> Copies)
{
    /// <summary>
    /// The selected upload cannot fit even after every other cloud copy is removed.
    /// </summary>
    public bool IsTargetOverLimit { get; private init; }

    /// <summary>
    /// Sum of logical cloud bytes removed by the proposed stops, not local file sizes.
    /// </summary>
    public long ReleasedBytes => Copies.Sum(item => item.Copy?.Bytes ?? 0);

    /// <summary>
    /// Size the request against today's quota, not the usage at its last rejection.
    /// </summary>
    public static long GetShortfall(ChatSyncStorageUsage usage, ChatSyncFailure? failure)
    {
        return failure?.Quota?.Shortfall(usage) ?? Math.Max(0, usage.UsedBytes - usage.LimitBytes);
    }

    /// <summary>
    /// Whether removing every other cloud copy would still leave this upload over quota.
    /// </summary>
    public static bool ExceedsLimitAlone(ChatSyncItem item, ChatSyncStorageUsage usage, long shortfall)
    {
        return shortfall > usage.UsedBytes - (item.Copy?.Bytes ?? 0);
    }

    /// <summary>
    /// Selects older, installed, fully synced copies by modification time without stopping the target upload.
    /// </summary>
    public static ChatSyncSpacePlan FromInventory(ChatSyncInventory inventory)
    {
        var usage = inventory.Usage;
        var blocked = inventory.Items
            .Where(item => item.Participation == ChatSyncParticipation.Cloud && item.Failure?.Quota?.BlocksUpload(usage) == true)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Id)
            .FirstOrDefault();
        var required = GetShortfall(usage, blocked?.Failure);
        if (required == 0 || usage.LimitBytes == 0)
        {
            return new ChatSyncSpacePlan(blocked?.Id, required, []);
        }

        if (blocked is not null && ExceedsLimitAlone(blocked, usage, required))
        {
            return new ChatSyncSpacePlan(blocked.Id, required, []) { IsTargetOverLimit = true };
        }

        var selected = new List<ChatSyncItem>();
        var remaining = required;
        var candidates = inventory.Items
            .AsValueEnumerable()
            .Where(item => item is { CanReleaseSpace: true, UpdatedAt: not null })
            .Where(item => blocked is null || item.UpdatedAt <= blocked.UpdatedAt)
            .OrderBy(item => item.UpdatedAt)
            .ThenBy(item => item.Id);
        foreach (var item in candidates)
        {
            if (item.Id == blocked?.Id || item.Copy is not { Bytes: > 0 } copy)
            {
                continue;
            }

            selected.Add(item);
            remaining -= copy.Bytes;
            if (remaining <= 0)
            {
                return new ChatSyncSpacePlan(blocked?.Id, required, selected);
            }
        }

        return new ChatSyncSpacePlan(blocked?.Id, required, []);
    }
}