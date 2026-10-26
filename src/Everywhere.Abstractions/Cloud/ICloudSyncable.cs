namespace Everywhere.Cloud;

/// <summary>
/// Describes the legacy v1 database entity shape retained for migration.
/// </summary>
public interface ICloudSyncable
{
    const long NotVersioned = 0;
    const long UnmodifiedFromCloud = -1;

    /// <summary>
    /// Guid v7 unique identifier for the entity.
    /// </summary>
    Guid Id { get; set; }

    /// <summary>
    /// Gets a value indicating whether the entity is marked as deleted (soft delete).
    /// </summary>
    bool IsDeleted { get; }

    /// <summary>
    /// The monotonic synchronization version number.
    /// <para>
    /// Older versions assigned this value through the synchronization save interceptor.
    /// 0 indicates the entity is not versioned/broken.
    /// -1 indicates the entity is pulled from the cloud but has never been modified locally.
    /// </para>
    /// </summary>
    long LocalSyncVersion { get; set; }
}