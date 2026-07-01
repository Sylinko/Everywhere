namespace Everywhere.Common;

/// <summary>
/// Provides functionality to detect runtime dependencies based on command names.
/// </summary>
public static class RuntimeDependencyDetector
{
    /// <summary>
    /// Detects the runtime dependency based on the provided command name.
    /// </summary>
    /// <param name="command"></param>
    /// <returns></returns>
    public static RuntimeDependency? Detect(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (LooksLikePath(command)) return null;

        var executableName = Path.GetFileNameWithoutExtension(command.Trim().Trim('"'));
        return executableName.ToLowerInvariant() switch
        {
            "uv" or "uvx" => new RuntimeDependency(RuntimeKind.Uv, executableName),
            "node" or "npm" or "npx" => new RuntimeDependency(RuntimeKind.NodeJs, executableName),
            "bun" or "bunx" => new RuntimeDependency(RuntimeKind.Bun, executableName),
            "docker" => new RuntimeDependency(RuntimeKind.Docker, executableName),
            _ => null
        };
    }

    public static bool LooksLikePath(string command)
    {
        return command.Contains(Path.DirectorySeparatorChar) ||
            command.Contains(Path.AltDirectorySeparatorChar) ||
            Path.IsPathFullyQualified(command);
    }
}
