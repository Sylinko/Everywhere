using System.Collections.Concurrent;
using Everywhere.Common;
using Everywhere.Utilities;
using MessagePack;
using Microsoft.Extensions.Logging;

namespace Everywhere.Configuration;

/// <summary>
/// A robust, high-performance key-value storage which persists data to disk in a binary format using MessagePack serialization.
/// </summary>
public sealed class PersistentKeyValueStorage : IKeyValueStorage, IAsyncInitializer, IDisposable
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Settings;

    private const string PrimaryExtension = ".bin";
    private const string TempExtension = ".tmp";

    private readonly string _primaryPath;
    private readonly string _tempPath;

    private readonly ILogger<PersistentKeyValueStorage> _logger;
    private readonly ConcurrentDictionary<string, byte[]> _store = new();
    private readonly DebounceExecutor<bool, ThreadingTimerImpl> _saveExecutor;

    private readonly Lock _fileLock = new();
    private volatile bool _isDisposed;
    private volatile bool _isLoaded;
    private int _isDirty;

    public PersistentKeyValueStorage(ILogger<PersistentKeyValueStorage> logger)
    {
        _logger = logger;

        _primaryPath = Path.Combine(RuntimeConstants.WritableFolderPath, "storage" + PrimaryExtension);
        _tempPath = Path.Combine(RuntimeConstants.WritableFolderPath, "storage" + TempExtension);

        _saveExecutor = new DebounceExecutor<bool, ThreadingTimerImpl>(
            () => true,
            _ => Save(),
            TimeSpan.FromSeconds(1));
    }

    public Task InitializeAsync()
    {
        Load();
        _isLoaded = true;
        return Task.CompletedTask;
    }

    public T? Get<T>(string key, T? defaultValue = default)
    {
        if (!_store.TryGetValue(key, out var bytes)) return defaultValue;

        try
        {
            return MessagePackSerializer.Deserialize<T>(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize key {Key}", key);
        }

        return defaultValue;
    }

    public void Set<T>(string key, T? value)
    {
        if (_isDisposed) return;

        if (value == null)
        {
            Remove(key);
            return;
        }

        try
        {
            var bytes = MessagePackSerializer.Serialize(value);

            if (bytes.Length > 256 * 1024)
            {
                throw new InvalidOperationException($"Serialized data for key {key} exceeds the size limit of 256KB.");
            }

            if (_store.TryGetValue(key, out var existingBytes) && existingBytes.AsSpan().SequenceEqual(bytes))
            {
                return;
            }

            _store[key] = bytes;
            Interlocked.Exchange(ref _isDirty, 1);
            _saveExecutor.Trigger();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to serialize key {Key}", key);
        }
    }

    public bool Contains(string key) => _store.ContainsKey(key);

    public void Remove(string key)
    {
        if (_isDisposed) return;

        if (_store.TryRemove(key, out _))
        {
            Interlocked.Exchange(ref _isDirty, 1);
            _saveExecutor.Trigger();
        }
    }

    private void Load()
    {
        if (!File.Exists(_primaryPath)) return;

        try
        {
            using var fileStream = new FileStream(_primaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Deserialize
            var loadedData = MessagePackSerializer.Deserialize<Dictionary<string, byte[]>?>(fileStream);
            if (loadedData is null) return;
            foreach (var kvp in loadedData)
            {
                _store[kvp.Key] = kvp.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load storage from {Path}", _primaryPath);
        }
    }

    private void Save()
    {
        if (_isDisposed || !_isLoaded) return;
        if (Interlocked.Exchange(ref _isDirty, 0) == 0) return;

        // Snapshot the data outside the lock to minimize blocking time.
        // ConcurrentDictionary is thread-safe for enumeration/snapshotting.
        Dictionary<string, byte[]> snapshot;
        try
        {
            snapshot = new Dictionary<string, byte[]>(_store);
        }
        catch
        {
            // In case of concurrent modification issues
            return;
        }

        lock (_fileLock)
        {
            try
            {
                var data = MessagePackSerializer.Serialize(snapshot);

                // Write to Temp
                using (var fileStream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fileStream.Write(data, 0, data.Length);
                    fileStream.Flush(true); // Ensure written to disk
                }

                // Atomic Move: Temp -> Primary (overwrite primary)
                File.Move(_tempPath, _primaryPath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save storage");
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _saveExecutor.Dispose();

        // Final save if there are unsaved changes
        if (Interlocked.Exchange(ref _isDirty, 0) == 1) Save();
    }
}