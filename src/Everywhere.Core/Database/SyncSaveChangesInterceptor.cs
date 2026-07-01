using Everywhere.Cloud;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ZLinq;

namespace Everywhere.Database;

/// <summary>
/// Intercepts EF Core save operations to automatically assign monotonic <see cref="ICloudSyncable.LocalSyncVersion"/> numbers.
/// This ensures that every added or modified entity gets a strictly increasing version number,
/// enabling robust cursor-based synchronization.
/// </summary>
public class SyncSaveChangesInterceptor : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        // Only operate on ChatDbContext instances that are not currently syncing.
        var context = eventData.Context;
        if (context is not ChatDbContext { IsSyncing: false }) return result;

        // 1. Identify all entities that are ISyncable and are being Added or Modified.
        var syncEntries = context.ChangeTracker.Entries<ICloudSyncable>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .ToList();

        // If no syncable entities are changed, proceed normally.
        if (syncEntries.Count == 0) return result;

        // 2. Fetch or Create the Global Sync Metadata.
        // We use a constant ID "Global" to ensure a singleton row.
        var meta = await context.Set<CloudSyncMetadataEntity>().FirstOrDefaultAsync(
            m => m.Id == CloudSyncMetadataEntity.SingletonId,
            cancellationToken: cancellationToken);
        if (meta is null) return result;

        // 3. Assign Version Numbers.
        // We increment the version for EACH entity individually.
        // This ensures strictly unique ordering, which prevents pagination issues
        // that can occur if multiple items share the exact same version number.
        foreach (var entry in syncEntries.AsValueEnumerable())
        {
            meta.LocalVersion++;
            entry.Entity.LocalSyncVersion = meta.LocalVersion;
        }

        // The 'meta' entity is now modified (via CurrentLocalVersion++),
        // so EF Core will automatically include it in the upcoming transaction commit.

        var changeCount = await base.SavingChangesAsync(eventData, result, cancellationToken);

        // Signal the cloud synchronizer that local data has changed.
        CloudSyncTrigger.SignalLocalChange();

        return changeCount;
    }
}