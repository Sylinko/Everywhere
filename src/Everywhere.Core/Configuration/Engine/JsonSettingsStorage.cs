using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Configuration.Engine;

/// <summary>
/// Identifies the kind of one segment in a JSON document path.
/// </summary>
/// <remarks>
/// This is intentionally different from <c>IConfiguration</c> paths. A segment named
/// <c>"0"</c> can be an object property, while array index <c>0</c> is represented
/// by <see cref="Index"/>.
/// </remarks>
public enum SettingsJsonPathSegmentKind
{
    /// <summary>
    /// A property on a JSON object.
    /// </summary>
    Property,

    /// <summary>
    /// An element index inside a JSON array.
    /// </summary>
    ArrayIndex
}

/// <summary>
/// Represents one typed segment in a settings JSON path.
/// </summary>
/// <remarks>
/// Use <see cref="Property"/> for JSON object members, including numeric property
/// names such as <c>"0"</c>. Use <see cref="ArrayIndex"/> only when the descriptor
/// proves the current node is a JSON array.
/// </remarks>
public readonly record struct SettingsJsonPathSegment
{
    private SettingsJsonPathSegment(SettingsJsonPathSegmentKind kind, string? propertyName, int arrayIndex)
    {
        Kind = kind;
        PropertyName = propertyName;
        ArrayIndex = arrayIndex;
    }

    /// <summary>
    /// Gets whether this segment navigates an object property or an array index.
    /// </summary>
    public SettingsJsonPathSegmentKind Kind { get; }

    /// <summary>
    /// Gets the JSON object property name when <see cref="Kind"/> is <see cref="SettingsJsonPathSegmentKind.Property"/>.
    /// </summary>
    public string? PropertyName { get; }

    /// <summary>
    /// Gets the JSON array index when <see cref="Kind"/> is <see cref="SettingsJsonPathSegmentKind.ArrayIndex"/>.
    /// </summary>
    public int ArrayIndex { get; }

    /// <summary>
    /// Creates a JSON object property path segment.
    /// </summary>
    /// <param name="name">The exact JSON property name.</param>
    public static SettingsJsonPathSegment Property(string name) =>
        string.IsNullOrEmpty(name) ?
            throw new ArgumentException("JSON property path segment cannot be null or empty.", nameof(name)) :
            new SettingsJsonPathSegment(SettingsJsonPathSegmentKind.Property, name, -1);

    /// <summary>
    /// Creates a JSON array index path segment.
    /// </summary>
    /// <param name="index">The zero-based array index.</param>
    public static SettingsJsonPathSegment Index(int index) =>
        index < 0 ?
            throw new ArgumentOutOfRangeException(nameof(index), index, "JSON array index cannot be negative.") :
            new SettingsJsonPathSegment(SettingsJsonPathSegmentKind.ArrayIndex, null, index);

    /// <summary>
    /// Gets the property name for a property segment, or throws if this segment is an array index.
    /// </summary>
    /// <remarks>
    /// The constructor enforces this invariant for values created through
    /// <see cref="Property"/> and <see cref="Index"/>. This helper still checks
    /// it at the boundary so callers do not need null-forgiving operators.
    /// </remarks>
    internal string GetPropertyName() =>
        Kind == SettingsJsonPathSegmentKind.Property && PropertyName is { Length: > 0 } propertyName ?
            propertyName :
            throw new InvalidOperationException("JSON path segment is not an object property segment.");

    public override string ToString() =>
        Kind == SettingsJsonPathSegmentKind.Property ? GetPropertyName() : $"[{ArrayIndex}]";
}

/// <summary>
/// Represents a typed path inside the settings JSON document.
/// </summary>
/// <remarks>
/// The path preserves the semantic difference between object properties and array
/// indexes. This prevents the settings engine from inheriting the string-first
/// flattening behavior of <c>IConfiguration</c>.
/// </remarks>
public readonly record struct SettingsJsonPath
{
    private readonly SettingsJsonPathSegment[] _segments;

    /// <summary>
    /// Creates a typed JSON path from the supplied path segments.
    /// </summary>
    public SettingsJsonPath(IEnumerable<SettingsJsonPathSegment> segments)
    {
        _segments = segments.ToArray();
    }

    /// <summary>
    /// Gets the root JSON document path.
    /// </summary>
    public static SettingsJsonPath Root { get; } = new([]);

    /// <summary>
    /// Gets the ordered typed path segments.
    /// </summary>
    public IReadOnlyList<SettingsJsonPathSegment> Segments => _segments ?? [];

    public override string ToString() =>
        Segments.Count == 0 ? "$" : string.Join('.', Segments.Select(s => s.ToString()));
}

/// <summary>
/// Stores the settings JSON document and persists it with debounced atomic writes.
/// </summary>
/// <remarks>
/// The save loop mirrors the proven concurrency pattern from
/// <c>WritableJsonConfigurationProvider</c>: readers and writers are guarded by a
/// <see cref="ReaderWriterLockSlim"/>, changes signal one background save loop, and
/// writes are performed through a temporary file. Unlike that provider, this class
/// never flattens data into <c>IConfiguration</c> strings; the in-memory model stays
/// a typed <see cref="JsonObject"/>.
/// </remarks>
public sealed class JsonSettingsStorage : IDisposable
{
    private const int DebounceMilliseconds = 200;
    private const int MaxWriteAttempts = 2;

    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _saveLoopTask;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly List<SettingsEngineDiagnostic> _diagnostics = []; // TODO: Bindable?
    private readonly Lock _diagnosticsLock = new();

    private JsonObject _jsonObj;
    private volatile bool _isDirty;
    private bool _disposed;

    private JsonSettingsStorage(
        string filePath,
        JsonObject root,
        ILogger? logger,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        FilePath = filePath;
        _jsonObj = root;
        _logger = logger ?? NullLogger.Instance;
        _jsonSerializerOptions = jsonSerializerOptions ?? SettingsEngineJson.SerializerOptions;

        // Start the background save loop task. The loop is signaled by logical JSON
        // changes and performs debounced atomic file writes.
        _saveLoopTask = Task.Run(SaveLoopAsync);
    }

    /// <summary>
    /// Gets the settings JSON file path.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets a thread-safe snapshot of diagnostics collected by this document store.
    /// </summary>
    public IReadOnlyList<SettingsEngineDiagnostic> Diagnostics
    {
        get
        {
            lock (_diagnosticsLock)
            {
                return _diagnostics.ToArray();
            }
        }
    }

    /// <summary>
    /// Loads a JSON-backed settings document store from disk.
    /// </summary>
    /// <param name="filePath">The settings JSON file path.</param>
    /// <param name="logger">An optional logger for parse and write failures.</param>
    public static JsonSettingsStorage Load(string filePath, ILogger? logger = null)
    {
        JsonObject? root = null;
        SettingsEngineDiagnostic? parseDiagnostic = null;

        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                root = JsonNode.Parse(json, null, SettingsEngineJson.DocumentOptions) as JsonObject;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                logger?.LogError(ex, "Failed to parse settings document: {FilePath}", filePath);
                parseDiagnostic = new SettingsEngineDiagnostic(
                    SettingsEngineDiagnosticKind.ParseFailure,
                    SettingsEngineDiagnosticSeverity.Error,
                    string.Empty,
                    new FormattedDynamicLocaleKey(LocaleKey.SettingsEngine_Diagnostic_ParseFailure, filePath),
                    ex);
            }
        }

        var store = new JsonSettingsStorage(filePath, root ?? new JsonObject(), logger);
        if (parseDiagnostic is not null)
        {
            store.AddDiagnostic(parseDiagnostic);
        }

        return store;
    }

    /// <summary>
    /// Creates a deep clone of the current JSON document.
    /// </summary>
    /// <returns>A detached snapshot safe for inspection outside the store lock.</returns>
    public JsonObject CreateSnapshot()
    {
        _lock.EnterReadLock();
        try
        {
            return (JsonObject)_jsonObj.DeepClone();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets a deep clone of a JSON node at the given typed path.
    /// </summary>
    /// <param name="path">A typed JSON path resolved from settings descriptors.</param>
    /// <returns>The cloned node, or <see langword="null"/> when the path is missing.</returns>
    public JsonNode? GetNode(SettingsJsonPath path)
    {
        _lock.EnterReadLock();
        try
        {
            return GetNodeCore(path)?.DeepClone();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Replaces a JSON subtree and schedules a debounced save.
    /// </summary>
    /// <param name="path">The typed JSON path to replace.</param>
    /// <param name="newNode">The new subtree. A <see langword="null"/> value writes JSON null.</param>
    public void ReplaceSubtree(SettingsJsonPath path, JsonNode? newNode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterWriteLock();
        try
        {
            ReplaceSubtreeCore(path, newNode);
            SignalChange();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Forces any pending changes to be written to disk.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token for the forced write.</param>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Only force save if there are actual changes.
        if (_isDirty)
        {
            await SaveSnapshotToFileAsync(force: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Performs a thread-safe edit of the JSON document and optionally signals a debounced save.
    /// </summary>
    /// <param name="editAction"></param>
    /// <param name="signalChange"></param>
    /// <exception cref="ObjectDisposedException"></exception>
    public void Edit(Action<JsonObject> editAction, bool signalChange)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lock.EnterWriteLock();
        try
        {
            editAction(_jsonObj);
            if (signalChange)
            {
                SignalChange();
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    internal void AddDiagnostic(SettingsEngineDiagnostic diagnostic)
    {
        lock (_diagnosticsLock)
        {
            _diagnostics.Add(diagnostic);
        }
    }

    private JsonNode? GetNodeCore(SettingsJsonPath path)
    {
        JsonNode? current = _jsonObj;
        foreach (var segment in path.Segments)
        {
            current = segment.Kind switch
            {
                SettingsJsonPathSegmentKind.Property when current is JsonObject obj &&
                    obj.TryGetPropertyValue(segment.GetPropertyName(), out var child) => child,
                SettingsJsonPathSegmentKind.ArrayIndex when current is JsonArray array &&
                    segment.ArrayIndex < array.Count => array[segment.ArrayIndex],
                _ => null
            };

            if (current is null) return null;
        }

        return current;
    }

    private void ReplaceSubtreeCore(SettingsJsonPath path, JsonNode? newNode)
    {
        var segments = path.Segments;
        if (segments.Count == 0)
        {
            if (newNode is null)
            {
                _jsonObj = new JsonObject();
                return;
            }

            _jsonObj = newNode.DeepClone() as JsonObject ??
                throw new InvalidOperationException("The settings document root must be a JSON object.");
            return;
        }

        JsonNode current = _jsonObj;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            current = EnsureContainer(current, segments[i], segments[i + 1]);
        }

        SetChild(current, segments[^1], newNode);
    }

    private static JsonNode EnsureContainer(
        JsonNode current,
        SettingsJsonPathSegment segment,
        SettingsJsonPathSegment nextSegment)
    {
        switch (segment.Kind)
        {
            case SettingsJsonPathSegmentKind.Property when current is JsonObject obj:
            {
                var propertyName = segment.GetPropertyName();
                if (!obj.TryGetPropertyValue(propertyName, out var child) || child is null)
                {
                    child = CreateContainer(nextSegment);
                    obj[propertyName] = child;
                }

                return child;
            }
            case SettingsJsonPathSegmentKind.ArrayIndex when current is JsonArray array:
            {
                while (array.Count <= segment.ArrayIndex)
                {
                    array.Add(null);
                }

                if (array[segment.ArrayIndex] is not { } child)
                {
                    child = CreateContainer(nextSegment);
                    array[segment.ArrayIndex] = child;
                }

                return child;
            }
            default:
            {
                throw new InvalidOperationException($"Cannot navigate JSON path segment '{segment}' through '{current.GetType().Name}'.");
            }
        }
    }

    private static void SetChild(JsonNode current, SettingsJsonPathSegment segment, JsonNode? node)
    {
        switch (segment.Kind)
        {
            case SettingsJsonPathSegmentKind.Property when current is JsonObject obj:
            {
                obj[segment.GetPropertyName()] = node?.DeepClone();
                break;
            }
            case SettingsJsonPathSegmentKind.ArrayIndex when current is JsonArray array:
            {
                while (array.Count <= segment.ArrayIndex)
                {
                    array.Add(null);
                }

                array[segment.ArrayIndex] = node?.DeepClone();
                break;
            }
            default:
            {
                throw new InvalidOperationException($"Cannot set JSON path segment '{segment}' on '{current.GetType().Name}'.");
            }
        }
    }

    private static JsonNode CreateContainer(SettingsJsonPathSegment nextSegment) =>
        nextSegment.Kind == SettingsJsonPathSegmentKind.ArrayIndex ? new JsonArray() : new JsonObject();

    /// <summary>
    /// Signals that the document changed and wakes the save loop.
    /// </summary>
    private void SignalChange()
    {
        _isDirty = true;

        // Release a signal if the count is 0. We don't need to accumulate every
        // signal; the dirty flag records that at least one write must happen.
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch
            {
                // The signal can race with disposal; the save loop will observe cancellation.
            }
        }
    }

    private async Task SaveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // 1. Wait for at least one logical JSON change.
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);

                // 2. Debounce to coalesce bursts of property changes.
                await Task.Delay(DebounceMilliseconds, _cts.Token).ConfigureAwait(false);

                // 3. Consume additional signals generated during the delay.
                while (_signal.CurrentCount > 0)
                {
                    await _signal.WaitAsync(0, _cts.Token).ConfigureAwait(false);
                }

                // 4. If changes remain, persist a consistent snapshot.
                if (_isDirty)
                {
                    await SaveSnapshotToFileAsync(force: false, _cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal disposal path.
        }
        catch (Exception ex)
        {
            // Prevent the background loop from crashing silently.
            RaiseWriteError(ex, 0, -1, FilePath);
        }
    }

    private Task SaveSnapshotToFileAsync(bool force, CancellationToken cancellationToken)
    {
        try
        {
            return DoAtomicWriteAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            RaiseWriteError(ex, 0, -1, FilePath);
            if (force) throw;
            return Task.CompletedTask;
        }
    }

    private async Task DoAtomicWriteAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        string content;
        _lock.EnterReadLock();
        try
        {
            content = _jsonObj.ToJsonString(_jsonSerializerOptions);

            // Key point: reset the dirty flag while holding the read lock. This
            // means the captured JSON text includes every change up to this point.
            // Any writer that runs after the lock is released will set _isDirty
            // again and signal another save.
            _isDirty = false;
        }
        finally
        {
            _lock.ExitReadLock();
        }

        var bytesLen = Encoding.UTF8.GetByteCount(content);
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = Path.Combine(dir ?? string.Empty, Path.GetRandomFileName());

        for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            try
            {
                await using (var fs = new FileStream(
                                 tempPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous))
                {
                    var buffer = Encoding.UTF8.GetBytes(content);
                    await fs.WriteAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempPath, FilePath, overwrite: true);
                return;
            }
            catch (OperationCanceledException)
            {
                SafeDelete(tempPath);
                break;
            }
            catch (Exception ex)
            {
                RaiseWriteError(ex, attempt, bytesLen, FilePath);
                SafeDelete(tempPath);
                if (attempt == MaxWriteAttempts) break;

                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                tempPath = Path.Combine(dir ?? string.Empty, Path.GetRandomFileName());
            }
        }
    }

    private void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temporary settings file '{TempPath}'", path);
        }
    }

    private void RaiseWriteError(Exception ex, int attempt, int contentLength, string filePath)
    {
        _logger.LogError(
            ex,
            "Failed to write settings JSON to '{FilePath}' (Attempt {Attempt}, Content Length: {ContentLength} bytes)",
            filePath,
            attempt,
            contentLength);

        AddDiagnostic(
            new SettingsEngineDiagnostic(
                SettingsEngineDiagnosticKind.WriteFailure,
                SettingsEngineDiagnosticSeverity.Error,
                string.Empty,
                new FormattedDynamicLocaleKey(LocaleKey.SettingsEngine_Diagnostic_WriteFileFailed, filePath, attempt, contentLength),
                ex));
    }

    /// <summary>
    /// Stops the save loop and releases synchronization primitives.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _cts.Cancel();
        try
        {
            _saveLoopTask.Wait();
        }
        catch
        {
            // Ignore cancellation and background-loop shutdown errors.
        }

        _cts.Dispose();
        _lock.Dispose();
        _signal.Dispose();
    }
}