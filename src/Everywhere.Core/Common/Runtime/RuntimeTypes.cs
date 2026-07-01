namespace Everywhere.Common;

public enum RuntimeKind
{
    Uv,
    NodeJs,
    Bun,
    Docker
}

public enum RuntimeSource
{
    None,
    Managed,
    System
}

public sealed record RuntimeDependency(RuntimeKind Kind, string Command)
{
    public string DisplayName => RuntimeManager.GetRuntimeDisplayName(Kind);
}

public sealed record RuntimeStatus(RuntimeKind Kind, RuntimeSource Source, string? ExecutablePath)
{
    public bool IsAvailable => Source != RuntimeSource.None && !string.IsNullOrWhiteSpace(ExecutablePath);
}

public interface IRuntimeManager
{
    event EventHandler? StatusChanged;

    bool HasRefreshed { get; }

    Task RefreshAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<string> GetPathEntries();

    RuntimeDependency? GetMissingDependency(string command);

    bool IsAvailable(RuntimeKind kind);

    /// <summary>
    /// Installs the runtime dependency. This may involve downloading and installing the runtime.
    /// </summary>
    /// <param name="kind"></param>
    /// <param name="progress"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task InstallAsync(RuntimeKind kind, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}
