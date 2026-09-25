namespace Everywhere.Common;

/// <summary>
/// Marks an object as a synchronization root for cooperating readers and writers.
/// </summary>
/// <remarks>
/// Callers lock the implementing instance around operations that require a stable read or an
/// uninterrupted batch of writes. Individual properties and methods do not implicitly acquire the lock.
/// </remarks>
public interface ISyncRoot;