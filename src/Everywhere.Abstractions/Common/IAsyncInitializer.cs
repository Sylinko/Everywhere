namespace Everywhere.Common;

/// <summary>
/// Smaller numbers are initialized first.
/// </summary>
public enum AsyncInitializerIndex
{
    Highest = int.MinValue,

    Database = 10,

    Settings = 100,

    Network = 200,

    Startup = int.MaxValue,
}

public interface IAsyncInitializer
{
    /// <summary>
    /// Smaller numbers are initialized first.
    /// </summary>
    AsyncInitializerIndex Index { get; }

    Task InitializeAsync();
}