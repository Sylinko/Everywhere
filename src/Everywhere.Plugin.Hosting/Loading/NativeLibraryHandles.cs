using System.Runtime.InteropServices;

namespace Everywhere.Plugin.Hosting.Loading;

/// <summary>
/// Owns only OS loader references acquired by the host resolver. This object deliberately holds no
/// collectible owner or delegate, so it can survive collection verification without preventing unloading.
/// </summary>
internal sealed class NativeLibraryHandles
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, nint> _handles = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Acquires one reference per resolved path even when simultaneous P/Invoke resolutions race.
    /// The supplied loader is invoked immediately and is never retained.
    /// </summary>
    internal nint Load(string path, Func<string, nint> load)
    {
        lock (_gate)
        {
            if (_handles.TryGetValue(path, out var existing)) return existing;
            var handle = load(path);
            _handles.Add(path, handle);
            return handle;
        }
    }

    /// <summary>
    /// Releases host-acquired references only after the corresponding managed context is confirmed collected.
    /// Native threads, callbacks and allocations must already have been stopped by the plugin's own cleanup.
    /// </summary>
    internal void ReleaseAfterCollection()
    {
        // The owner is already collected, so no resolver can access this dictionary concurrently.
        foreach (var handle in _handles.Values) NativeLibrary.Free(handle);
        _handles.Clear();
    }
}

/// <summary>
/// Keeps native ownership separate from the weak observation of a retired managed context.
/// </summary>
/// <param name="Context">The weak observation used to confirm that managed ownership has ended.</param>
/// <param name="NativeLibraries">The host-acquired references retained until collection is confirmed.</param>
internal sealed record AssemblyRetirement(WeakReference Context, NativeLibraryHandles NativeLibraries);