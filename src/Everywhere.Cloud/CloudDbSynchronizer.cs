using System.Net.Sockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicData.Binding;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Database;
using Everywhere.Extensions;
using MessagePack;
using MessagePack.Formatters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Everywhere.Cloud;

public sealed partial class CloudChatDbSynchronizer(
    IDbContextFactory<ChatDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    ICloudClient cloudClient,
    PersistentState persistentState,
    ILogger<CloudChatDbSynchronizer> logger
) : ObservableObject, IChatDbSynchronizer, IAsyncInitializer, IDisposable
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Network + 1; // after persistentState initialization

    [ObservableProperty]
    public partial bool IsCloudSyncing { get; private set; }

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly CompositeDisposable _disposables = new();
    private const int PushBytesLimit = 5 * 1024 * 1024; // 5 MB

    // Adaptive delay state
    private int _consecutiveIdleCount;
    private int _consecutiveErrorCount;

    private static readonly TimeSpan[] IdleBackoffSchedule =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)];

    private static readonly TimeSpan[] ErrorBackoffSchedule =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)];

    public Task InitializeAsync()
    {
        // If the cloud sync base URL is not configured, skip synchronization.
        if (CloudConstants.CloudSyncBaseUrl.IsNullOrEmpty()) return Task.CompletedTask;

        // Use System.Reactive to observe changes
        // If cloudClient.CurrentUser is not null && persistentState.IsCloudSyncEnabled, start synchronization.
        // Otherwise, cancel the token
        cloudClient.WhenPropertyChanged(x => x.UserProfile)
            .CombineLatest(
                persistentState.WhenPropertyChanged(x => x.IsCloudSyncEnabled),
                (user, enabled) => user.Value is not null && enabled.Value)
            .StartWith(cloudClient.UserProfile is not null && persistentState.IsCloudSyncEnabled)
            .DistinctUntilChanged()
            .Select(isReady => Observable.FromAsync(cancellationToken => isReady ? StartSyncAsync(cancellationToken) : Task.CompletedTask))
            .Switch() // Only allow one active synchronization task at a time, cancel previous if new value comes in
            .Subscribe()
            .AddTo(_disposables);

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _disposables.Dispose();
        _syncLock.Dispose();
    }

    private Task StartSyncAsync(CancellationToken cancellationToken) => Task.Run(
        async () =>
        {
            try
            {
                // Throttle, wait for a few seconds before starting the first synchronization
                // But cancel immediately if cancellation is requested to avoid unnecessary waiting when user quickly toggles the sync setting.
                await Task.Delay(TimeSpan.FromSeconds(3d), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ignored
            }

            _consecutiveIdleCount = 0;
            _consecutiveErrorCount = 0;

            while (true)
            {
                SyncOutcome syncOutcome;
                try
                {
                    IsCloudSyncing = true;
                    var result = await SynchronizeCoreAsync(cancellationToken).ConfigureAwait(false);

                    persistentState.LastCloudSynchronizationErrorMessageKey = null;
                    persistentState.LastCloudSynchronized = DateTimeOffset.UtcNow;
                    _consecutiveErrorCount = 0;

                    if (result.HadRemoteChanges)
                    {
                        syncOutcome = SyncOutcome.RemoteChanges;
                        _consecutiveIdleCount = 0;
                    }
                    else if (result.HadLocalChanges)
                    {
                        syncOutcome = SyncOutcome.LocalChanges;
                        _consecutiveIdleCount = 0;
                    }
                    else
                    {
                        syncOutcome = SyncOutcome.Idle;
                        _consecutiveIdleCount++;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpRequestException ex) when (ex.InnerException is SocketException)
                {
                    // Network-related errors
                    var handledException = HandledSystemException.Handle(ex);
                    persistentState.LastCloudSynchronizationErrorMessageKey = handledException.GetFriendlyMessage();
                    syncOutcome = SyncOutcome.NetworkError;
                    _consecutiveErrorCount++;
                    _consecutiveIdleCount = 0;

                    logger.LogInformation(handledException, "Network error occurred during cloud database synchronization.");
                }
                catch (Exception ex)
                {
                    ex = HandledSystemException.Handle(ex);
                    persistentState.LastCloudSynchronizationErrorMessageKey = ex.GetFriendlyMessage();
                    syncOutcome = SyncOutcome.Error;
                    _consecutiveErrorCount++;
                    _consecutiveIdleCount = 0;

                    logger.LogError(ex, "Error occurred during cloud database synchronization.");
                }
                finally
                {
                    IsCloudSyncing = false;
                }

                var nextDelay = CalculateNextDelay(syncOutcome);
                logger.LogDebug(
                    "Sync outcome: {Outcome}, next delay: {Delay} (idle={IdleCount}, error={ErrorCount})",
                    syncOutcome, nextDelay, _consecutiveIdleCount, _consecutiveErrorCount);

                try
                {
                    // Wait for either the adaptive delay or a local data change signal, whichever comes first.
                    await Task.WhenAny(
                        Task.Delay(nextDelay, cancellationToken),
                        CloudSyncTrigger.WaitForChangeAsync(cancellationToken)
                    ).ConfigureAwait(false);

                    // If woken by a local change signal, debounce briefly to batch rapid changes.
                    await Task.Delay(TimeSpan.FromSeconds(10d), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Ignored
                }
            }
        },
        cancellationToken);

    private TimeSpan CalculateNextDelay(SyncOutcome outcome) => outcome switch
    {
        SyncOutcome.RemoteChanges => TimeSpan.FromSeconds(15),
        SyncOutcome.LocalChanges => TimeSpan.FromMinutes(1),
        SyncOutcome.Idle => IdleBackoffSchedule[Math.Min(_consecutiveIdleCount, IdleBackoffSchedule.Length - 1)],
        SyncOutcome.NetworkError => ErrorBackoffSchedule[Math.Min(_consecutiveErrorCount, ErrorBackoffSchedule.Length - 1)],
        SyncOutcome.Error => ErrorBackoffSchedule[Math.Min(_consecutiveErrorCount, ErrorBackoffSchedule.Length - 1)],
        _ => TimeSpan.FromMinutes(1)
    };

    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
        => await SynchronizeCoreAsync(cancellationToken);

    private async Task<SyncResult> SynchronizeCoreAsync(CancellationToken cancellationToken)
    {
        using var _ = await _syncLock.LockAsync(cancellationToken);

        await using var dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);
        var metadata = await dbContext.SyncMetadata.FirstOrDefaultAsync(
            x => x.Id == CloudSyncMetadataEntity.SingletonId,
            cancellationToken: cancellationToken);
        if (metadata is null) return default;

        // Use named HttpClient for ICloudClient to ensure proper configuration (e.g., authentication, proxy).
        using var httpClient = httpClientFactory.CreateClient(nameof(ICloudClient));
        httpClient.Timeout = TimeSpan.FromMinutes(5); // Set a reasonable timeout for sync operations

        bool hadRemoteChanges;
        try
        {
            // 1. Pull remote changes from the cloud
            // set syncing flag to avoid interference with other operations
            dbContext.IsSyncing = true;
            hadRemoteChanges = await PullChangesAsync(dbContext, metadata, httpClient, cancellationToken);
        }
        finally
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.IsSyncing = false;
        }

        bool hadLocalChanges;
        try
        {
            // 2. Push local changes to the cloud
            hadLocalChanges = await PushChangesAsync(dbContext, metadata, httpClient, cancellationToken);
        }
        finally
        {
            // save metadata changes
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new SyncResult(hadRemoteChanges, hadLocalChanges);
    }

    private async ValueTask<bool> PullChangesAsync(
        ChatDbContext dbContext,
        CloudSyncMetadataEntity metadata,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var hadChanges = false;
        while (true)
        {
            logger.LogDebug("Pulling cloud changes since version {LastPulledVersion}", metadata.LastPulledVersion);

            var response = await httpClient.GetAsync(
                new Uri($"{CloudConstants.CloudSyncBaseUrl}/chat-db/pull?sinceVersion={Math.Max(metadata.LastPulledVersion, 0L)}"),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // This will throw an exception with detailed error info.
                await ApiPayload.EnsureSuccessFromHttpResponseJsonAsync(response, cancellationToken: cancellationToken);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            if (stream is null)
            {
                throw new HttpRequestException("Failed to deserialize cloud pull response payload.");
            }

            var data = await MessagePackSerializer.DeserializeAsync<CloudPullData>(
                stream,
                cancellationToken: cancellationToken);

            // Apply pulled version regardless of whether there are items to process.
            metadata.LastPulledVersion = data.LatestVersion;
            metadata.LastSyncAt = DateTimeOffset.UtcNow;

            if (data.EntityWrappers is not { Count: > 0 }) return hadChanges;

            hadChanges = true;
            var chats = dbContext.Chats;
            var nodes = dbContext.Nodes;
            foreach (var entityWrapper in data.EntityWrappers)
            {
                if (entityWrapper.Data is null)
                {
                    var existedNode = await FindEntityAsync(entityWrapper.Id);
                    if (existedNode is not null)
                    {
                        existedNode.IsDeleted = true;
                        existedNode.LocalSyncVersion = ICloudSyncable.UnmodifiedFromCloud;
                    }
                    else
                    {
                        logger.LogInformation("Received delete for non-existing entity Id {EntityId} from cloud.", entityWrapper.Id);
                    }
                }
                else
                {
                    EntityData entityData;
                    try
                    {
                        entityData = MessagePackSerializer.Deserialize<EntityData>(
                            entityWrapper.Data,
                            cancellationToken: cancellationToken).NotNull();
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to deserialize a pulled cloud entity.");
                        continue;
                    }

                    if (entityWrapper.Id == Guid.Empty)
                    {
                        // Invalid Id, skip it.
                        continue;
                    }

                    if (entityData.EncryptionType != CloudSyncEncryptionType.None)
                    {
                        // Not supported encryption type, skip it.
                        logger.LogWarning(
                            "Received an entity with unsupported encryption type {EncryptionType} from cloud, skipping it.",
                            entityData.EncryptionType);
                        continue;
                    }

                    // entity.Id is not serialized/deserialized, so we need to set it manually.
                    var entity = entityData.Entity;
                    entity.Id = entityWrapper.Id;
                    entity.LocalSyncVersion = ICloudSyncable.UnmodifiedFromCloud;

                    var existingEntity = await FindEntityAsync(entity.Id);
                    if (existingEntity is null)
                    {
                        switch (entity)
                        {
                            case ChatContextEntity chat:
                            {
                                await chats.AddAsync(chat, cancellationToken);
                                break;
                            }
                            case ChatNodeEntity node:
                            {
                                await nodes.AddAsync(node, cancellationToken);
                                break;
                            }
                        }
                    }
                    else if (existingEntity.LocalSyncVersion <= metadata.LastPushedVersion)
                    {
                        // Only update(replace) if the local entity is unmodified since last pull
                        // In the other words, if existedEntity.SyncVersion > metadata.LastPushedVersion,
                        // it means the local entity has been modified locally after last pull,
                        // so we should not overwrite it with the pulled entity.
                        // It needs to be pushed again in the next sync cycle.
                        dbContext.Entry(existingEntity).CurrentValues.SetValues(entity);
                    }
                }
            }

            if (!data.HasMore) break;
        }

        return hadChanges;

        // Find nodes first because it has a higher chance of being requested.
        async Task<CloudSyncableEntity?> FindEntityAsync(Guid id) =>
            await dbContext.Nodes.FirstOrDefaultAsync(x => x.Id == id, cancellationToken: cancellationToken) as CloudSyncableEntity ??
            await dbContext.Chats.FirstOrDefaultAsync(x => x.Id == id, cancellationToken: cancellationToken);
    }

    private async ValueTask<bool> PushChangesAsync(
        ChatDbContext dbContext,
        CloudSyncMetadataEntity metadata,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var lastPushedVersion = metadata.LastPushedVersion;
        var currentPushedVersion = lastPushedVersion;

        // Gather all entities that have LocalSyncVersion > lastPushedVersion (modified since last push)
        // Since EF Core does not support polymorphic queries, we need to query each DbSet separately.
        var entitiesToPush = new List<CloudSyncableEntity>();
        entitiesToPush.AddRange(
            await dbContext.Chats
                .AsNoTracking()
                .Where(x => x.LocalSyncVersion > lastPushedVersion)
                .Where(x => x.Id != Guid.Empty)
                .ToListAsync(cancellationToken: cancellationToken));
        entitiesToPush.AddRange(
            await dbContext.Nodes
                .AsNoTracking()
                .Where(x => x.LocalSyncVersion > lastPushedVersion)
                .Where(x => x.Id != Guid.Empty)
                .ToListAsync(cancellationToken: cancellationToken));
        if (entitiesToPush.Count == 0) return false;

        var entityWrappers = new List<EntityWrapper>(entitiesToPush.Count);
        var totalPushBytes = 0;

        // Sort entities by LocalSyncVersion in ascending order to push older changes first.
        // Even though a chunk is failed to push, metadata.LastPushedVersion will be updated to the highest
        // successfully pushed version, so the next sync cycle will retry the failed entities.
        entitiesToPush.Sort((x, y) => x.LocalSyncVersion.CompareTo(y.LocalSyncVersion));

        foreach (var entity in entitiesToPush)
        {
            EntityWrapper entityWrapper;
            if (entity.IsDeleted)
            {
                // If the entity is marked as deleted, we only need to send its Id and null data.
                entityWrapper = new EntityWrapper(entity.Id, null);
            }
            else
            {
                var data = MessagePackSerializer.Serialize(
                    new EntityData(CloudSyncEncryptionType.None, entity),
                    cancellationToken: cancellationToken);
                if (data.Length > PushBytesLimit)
                {
                    logger.LogWarning(
                        "Entity [{EntityType}] {Entity} exceeds the push size limit and will be skipped.",
                        entity.GetType().Name,
                        entity.ToString());

                    entity.LocalSyncVersion = ICloudSyncable.NotVersioned;
                    continue;
                }

                entityWrapper = new EntityWrapper(entity.Id, data);
            }

            var entrySize = (entityWrapper.Data?.Length ?? 0) + 64; // Approximate overhead for Id and IsDeleted

            // Flush the current chunk BEFORE it would exceed the limit, so no single POST goes over.
            if (entityWrappers.Count > 0 && totalPushBytes + entrySize > PushBytesLimit)
            {
                await PushPayloadAsync(entityWrappers, httpClient, cancellationToken);
                entityWrappers.Clear();
                totalPushBytes = 0;
                metadata.LastPushedVersion = currentPushedVersion; // If successful, update the last pushed version
                metadata.LastSyncAt = DateTimeOffset.UtcNow;
            }

            entityWrappers.Add(entityWrapper);
            totalPushBytes += entrySize;
            currentPushedVersion = Math.Max(entity.LocalSyncVersion, currentPushedVersion); // Update the current pushed version
        }

        // Push any remaining entities.
        if (entityWrappers.Count > 0)
        {
            await PushPayloadAsync(entityWrappers, httpClient, cancellationToken);
            metadata.LastPushedVersion = currentPushedVersion; // If successful, update the last pushed version
            metadata.LastSyncAt = DateTimeOffset.UtcNow;
        }

        return true;
    }

    /// <summary>
    /// Pushes local changes to the remote cloud storage.
    /// </summary>
    /// <param name="payload"></param>
    /// <param name="httpClient"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>The highest SyncVersion that was successfully pushed.</returns>
    private async ValueTask PushPayloadAsync(List<EntityWrapper> payload, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var data = MessagePackSerializer.Serialize(payload, cancellationToken: cancellationToken);
        var response = await httpClient.PostAsync(
            new Uri($"{CloudConstants.CloudSyncBaseUrl}/chat-db/push"),
            new ByteArrayContent(data),
            cancellationToken);
        var result = await ApiPayload.EnsureSuccessFromHttpResponseJsonAsync(response, cancellationToken: cancellationToken);
        logger.LogDebug("Pushed {EntityCount} entities to cloud successfully: {Result}", payload.Count, result);
    }
}

[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial record EntityData(
    [property: Key(0)] CloudSyncEncryptionType EncryptionType,
    [property: Key(1)] CloudSyncableEntity Entity
);

/// <summary>
/// Wrapper for an entity to be synchronized.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial record EntityWrapper(
    [property: Key(0), MessagePackFormatter(typeof(NativeGuidFormatter))] Guid Id,
    [property: Key(1)] byte[]? Data // Serialized EntityData
);

/// <summary>
/// Api payload for cloud pull response.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class CloudPullData
{
    [Key(0)]
    public long LatestVersion { get; set; }

    [Key(1)]
    public bool HasMore { get; set; }

    [Key(2)]
    public List<EntityWrapper>? EntityWrappers { get; set; }
}

internal readonly record struct SyncResult(bool HadRemoteChanges, bool HadLocalChanges);

internal enum SyncOutcome
{
    Idle,
    LocalChanges,
    RemoteChanges,
    NetworkError,
    Error
}
