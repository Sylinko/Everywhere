using Everywhere.Database;
using Everywhere.Interop;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Storage;

/// <summary>
/// Owns the upgrade acknowledgment independently of the transactional legacy import.
/// Trashing the previous-version database is an explicit optional action, never startup cleanup.
/// </summary>
public sealed class ChatStoreUpgradeService(
    IDbContextFactory<ChatV2DbContext> dbFactory,
    string legacyDatabasePath,
    INativeHelper nativeHelper)
{
    /// <summary>
    /// Gets whether a committed import still needs to be explained to the user.
    /// </summary>
    public async Task<bool> NeedsNoticeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.StoreInfo.AnyAsync(
            row => row.LegacyImportedAt != null && row.UpgradeNoticeAcknowledgedAt == null,
            cancellationToken);
    }

    /// <summary>
    /// Records acknowledgment without rerunning migration or depending on successful old-file removal.
    /// </summary>
    public async Task AcknowledgeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.StoreInfo.Where(row => row.LegacyImportedAt != null && row.UpgradeNoticeAcknowledgedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.UpgradeNoticeAcknowledgedAt, DateTimeOffset.UtcNow),
                cancellationToken);
    }

    /// <summary>
    /// Trashes only the explicitly selected previous database and its SQLite sidecars, after a
    /// completed import. Attachment files are shared with v2 and are never removed here.
    /// A failure is reported to the caller; it cannot damage or invalidate the new database.
    /// </summary>
    public async Task TrashLegacyDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.StoreInfo.AnyAsync(row => row.LegacyImportedAt != null, cancellationToken))
        {
            throw new InvalidOperationException("The previous database has not been imported.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Keep the main file first. WAL may contain uncheckpointed data and must also be recoverable.
        var paths = new[] { legacyDatabasePath, legacyDatabasePath + "-wal", legacyDatabasePath + "-shm" }
            .Where(File.Exists)
            .ToArray();
        await nativeHelper.MoveToTrashAsync(paths, cancellationToken);
    }
}