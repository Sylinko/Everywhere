using System.Runtime.CompilerServices;

namespace Everywhere.Core.Tests;

/// <summary>
/// Redirects <see cref="Everywhere.Common.RuntimeConstants"/> directories into a process-scoped temporary root
/// before any test assembly code triggers static initialization, preventing test runs from writing blobs or
/// empty chat workspaces into the developer's local profile directories.
/// </summary>
internal static class TestRuntimeEnvironment
{
    private static string? _rootDirectory;

    [ModuleInitializer]
    internal static void Initialize()
    {
        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "Everywhere.Tests",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        _rootDirectory = rootDirectory;

        SetIfUnset("EVERYWHERE_DATA_DIR", Path.Combine(rootDirectory, "data"));
        SetIfUnset("EVERYWHERE_CACHE_DIR", Path.Combine(rootDirectory, "cache"));
        SetIfUnset("EVERYWHERE_USER_DIR", Path.Combine(rootDirectory, "user"));

        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            if (_rootDirectory is not { Length: > 0 } path || !Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup; remaining handles will release at OS process termination.
            }
        };
    }

    private static void SetIfUnset(string variableName, string value)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName)))
        {
            Environment.SetEnvironmentVariable(variableName, value);
        }
    }
}
