namespace Everywhere.Common;

/// <summary>
/// Smaller numbers are initialized first.
/// </summary>
public enum AsyncInitializerIndex
{
    Highest = int.MinValue,

    MainHostControl = Highest,

    Database = 10,

    Settings = 100,

    HostProcesses = Settings + 1,

    Network = 200,

    Startup = int.MaxValue,
}

public interface IAsyncInitializer
{
    /// <summary>
    /// Smaller numbers are initialized first.
    /// </summary>
    AsyncInitializerIndex Index { get; }

    /// <summary>
    /// Initializes the service as part of application startup.
    /// </summary>
    /// <param name="cancellationToken">Cancels the current startup sequence. Long-running service lifetimes must use service-owned cancellation.</param>
    Task InitializeAsync(CancellationToken cancellationToken);
}