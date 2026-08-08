#if IsMacOS || IsLinux
using System.Diagnostics;
#endif

using System.Text;
using Serilog;

namespace Everywhere.Interop;

public static class EnvironmentVariableUtilities
{
#if IsMacOS || IsLinux
    private static string? _cachedUnixPath;
    private static DateTime _unixPathCacheTimestamp;
    private static readonly Lock UnixPathCacheLock = new();
    private static readonly TimeSpan UnixPathCacheTtl = TimeSpan.FromSeconds(10);
#endif

    /// <summary>
    /// Gets the latest PATH environment variable by merging Machine, User and Process level PATHs.
    /// On macOS and Linux, it uses the user's login shell to resolve the true PATH.
    /// </summary>
    /// <returns></returns>
    public static string? GetLatestPathVariable()
    {
        try
        {
            var sb = new StringBuilder();
            var existingPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            void AppendPaths(string? sourcePath)
            {
                if (string.IsNullOrEmpty(sourcePath)) return;
                var parts = sourcePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var part in parts)
                {
                    if (existingPaths.Add(part))
                    {
                        if (sb.Length > 0) sb.Append(Path.PathSeparator);
                        sb.Append(part);
                    }
                }
            }

#if IsWindows
            AppendPaths(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
            AppendPaths(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
#elif IsMacOS || IsLinux
            AppendPaths(GetUnixLoginShellPath());
#else
            #error "Platform not supported."
#endif

            var currentProcessPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process)
                ?? Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Process);
            AppendPaths(currentProcessPath);

            return sb.ToString();
        }
        catch (Exception ex)
        {
            Log.ForContext(typeof(EnvironmentVariableUtilities)).Error(ex, "Failed to get latest PATH environment variable.");

            // Ignore errors when trying to refresh environment
            return null;
        }
    }

#if IsMacOS || IsLinux
    private static string? GetUnixLoginShellPath()
    {
        if (DateTime.UtcNow - _unixPathCacheTimestamp < UnixPathCacheTtl) return _cachedUnixPath;

        lock (UnixPathCacheLock)
        {
            if (DateTime.UtcNow - _unixPathCacheTimestamp < UnixPathCacheTtl) return _cachedUnixPath;

            try
            {
                // ── SHELL INJECTION GUARD ────────────────────────────────────────────
                // The $SHELL environment variable is controlled by the user (or an attacker).
                // Blindly spawning it as a process risks arbitrary code execution (CWE-78).
                // Only allow well-known shell binaries; fall back to a safe platform default
                // if $SHELL contains an unexpected value.
                var shellFromEnv = Environment.GetEnvironmentVariable("SHELL");
                var allowedShells = new HashSet<string>(StringComparer.Ordinal)
                {
                    "/bin/sh", "/bin/bash", "/bin/zsh", "/bin/fish",
                    "/usr/bin/bash", "/usr/bin/zsh", "/usr/bin/fish",
                    "/usr/local/bin/bash", "/usr/local/bin/zsh", "/usr/local/bin/fish"
                };

                string shell;
                if (!string.IsNullOrEmpty(shellFromEnv) && allowedShells.Contains(shellFromEnv))
                {
                    shell = shellFromEnv;
                }
                else
                {
                    // Unsafe or empty $SHELL — silently fall back to platform default.
                    shell = OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";
                    if (!string.IsNullOrEmpty(shellFromEnv))
                    {
                        Log.ForContext(typeof(EnvironmentVariableUtilities))
                           .Warning("$SHELL value '{Shell}' is not in the allowed list and will be ignored.", shellFromEnv);
                    }
                }
                // ─────────────────────────────────────────────────────────────────────

                using var process = Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = shell,
                        Arguments = "-lc \"echo $PATH\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                );
                if (process is null) return null;

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);

                // Extract the last meaningful line, in case of shell startup noise.
                var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                for (var i = lines.Length - 1; i >= 0; i--)
                {
                    var line = lines[i].Trim();
                    if (line.Contains(Path.PathSeparator))
                    {
                        _cachedUnixPath = line;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(_cachedUnixPath) && lines.Length > 0)
                {
                    _cachedUnixPath = lines.Last().Trim();
                }
            }
            catch (Exception ex)
            {
                Log.ForContext(typeof(EnvironmentVariableUtilities)).Warning(ex, "Failed to fetch login shell PATH.");
            }

            _unixPathCacheTimestamp = DateTime.UtcNow;
            return _cachedUnixPath;
        }
    }
#endif
}