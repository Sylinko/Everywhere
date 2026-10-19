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

/// <summary>
/// Performs one startup step. Construction and initialization run on a background thread;
/// implementations must explicitly dispatch UI work and support startup cancellation.
/// </summary>
/// <remarks>
/// Constructors must not require an earlier startup step to have completed. UI-owned objects
/// must be resolved inside cancellable dispatcher operations, not constructor dependencies.
/// Background work may keep using the application-lifetime token after initialization completes.
/// A service that must wait for its work before releasing resources does so in its own disposal.
/// </remarks>
public interface IAsyncInitializer
{
    /// <summary>
    /// Smaller numbers are initialized first.
    /// </summary>
    static abstract AsyncInitializerIndex Index { get; }

    /// <summary>
    /// Initializes the service as part of application startup.
    /// </summary>
    /// <param name="cancellationToken">The application-lifetime token, canceled on exit or startup failure. It remains valid after this method completes.</param>
    Task InitializeAsync(CancellationToken cancellationToken);
}