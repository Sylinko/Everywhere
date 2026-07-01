namespace Everywhere.Cloud;

/// <summary>
/// Defines a contract for entities that must be synchronized with the remote cloud storage.
/// Implementing this interface allows the SyncSaveChangesInterceptor to automatically
/// assign monotonic version numbers upon modification.
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
    /// This value is automatically managed by the database context interceptor.
    /// It increments globally for every insert or update operation across the database.
    /// 0 indicates the entity is not versioned/broken.
    /// -1 indicates the entity is pulled from the cloud but has never been modified locally.
    /// </para>
    /// </summary>
    long LocalSyncVersion { get; set; }
}