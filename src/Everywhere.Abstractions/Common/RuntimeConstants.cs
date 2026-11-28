using System.Buffers;

namespace Everywhere.Common;

/// <summary>
/// Provides runtime constants and utility methods for managing application-internal data, cache, and user-facing paths.
/// </summary>
public static partial class RuntimeConstants
{
    private static readonly SearchValues<char> InvalidFileNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>
    /// Get a unique device identifier for the current machine with length of 36 characters (GUID format).
    /// The device ID is generated once and stored in the writable data path, ensuring that it remains consistent across application sessions on the same machine.
    /// This can be used for telemetry, analytics, or any scenario where a consistent identifier for the device is needed.
    /// </summary>
    public static string DeviceId { get; }

    /// <summary>
    /// Gets the writable data path for application-internal state, ensuring that the directory exists.
    /// This path stores internal files such as databases, logs, blobs, and runtime binaries that users do not normally access directly.
    /// </summary>
    public static string WritableFolderPath { get; }

    /// <summary>
    /// Gets the writable folder used for portable runtime binaries managed by Everywhere.
    /// </summary>
    public static string BinFolderPath { get; }

    /// <summary>
    /// Gets the cache path for the application.
    /// This path holds disposable caches and temporary downloads that can be deleted at any time without corrupting user or application state.
    /// </summary>
    public static string CacheFolderPath { get; }

    /// <summary>
    /// Gets the user- and agent-facing root folder (<c>~/.everywhere</c> by default), ensuring that the directory exists.
    /// This folder holds short-path artifacts meant for users and agents, such as skills, chat workspaces, and isolated sessions.
    /// </summary>
    public static string UserFolderPath { get; }

    static RuntimeConstants()
    {
        DeviceId = EnsureDeviceId();
        WritableFolderPath = EnsureDirectory(
            GetOverriddenPath("EVERYWHERE_DATA_DIR") ??
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData,
                    Environment.SpecialFolderOption.DoNotVerify),
                "Everywhere"));
        BinFolderPath = EnsureDirectory(Path.Combine(WritableFolderPath, "bin"));
        CacheFolderPath = EnsureDirectory(
            GetOverriddenPath("EVERYWHERE_CACHE_DIR") ??
            Path.Combine(WritableFolderPath, "cache"));
        UserFolderPath = EnsureDirectory(
            GetOverriddenPath("EVERYWHERE_USER_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".everywhere"));
    }

    private static string? GetOverriddenPath(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Ensures that the specified relative path within the writable data folder exists and returns the full path.
    /// </summary>
    /// <param name="relativePaths"></param>
    /// <returns></returns>
    public static string EnsureWritableDataFolderPath(params ReadOnlySpan<string> relativePaths)
    {
        return EnsureDirectory(Path.GetFullPath(Path.Combine([WritableFolderPath, ..relativePaths])));
    }

    /// <summary>
    /// Ensures that the specified relative path within the cache folder exists and returns the full path.
    /// </summary>
    /// <param name="relativePaths"></param>
    /// <returns></returns>
    public static string EnsureCacheFolderPath(params ReadOnlySpan<string> relativePaths)
    {
        return EnsureDirectory(Path.GetFullPath(Path.Combine([CacheFolderPath, ..relativePaths])));
    }

    /// <summary>
    /// Gets the full path for the specified relative segments within <see cref="UserFolderPath"/>
    /// without creating directories on disk.
    /// </summary>
    /// <param name="relativePaths">Relative path segments beneath <see cref="UserFolderPath"/>.</param>
    /// <returns>The resolved full path.</returns>
    public static string GetUserFolderPath(params ReadOnlySpan<string> relativePaths)
    {
        return Path.GetFullPath(Path.Combine([UserFolderPath, ..relativePaths]));
    }

    /// <summary>
    /// Ensures that the specified relative path within <see cref="UserFolderPath"/> exists and returns the full path.
    /// </summary>
    /// <param name="relativePaths">Relative path segments beneath <see cref="UserFolderPath"/>.</param>
    /// <returns>The ensured full directory path.</returns>
    public static string EnsureUserFolderPath(params ReadOnlySpan<string> relativePaths)
    {
        return EnsureDirectory(GetUserFolderPath(relativePaths));
    }

    /// <summary>
    /// Ensures that an isolated session folder under <c>sessions/&lt;category&gt;/&lt;sessionId&gt;</c>
    /// within <see cref="UserFolderPath"/> exists and returns the full path.
    /// </summary>
    /// <param name="category">Single-segment session category name (e.g. <c>mod-host</c>).</param>
    /// <param name="sessionId">Single-segment session identifier.</param>
    /// <returns>The ensured full directory path.</returns>
    public static string EnsureSessionFolderPath(string category, string sessionId)
    {
        ValidatePathSegment(category, nameof(category));
        ValidatePathSegment(sessionId, nameof(sessionId));
        return EnsureDirectory(Path.GetFullPath(Path.Combine(UserFolderPath, "sessions", category, sessionId)));
    }

    private static void ValidatePathSegment(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        if (value is "." or ".." ||
            value.Contains('/') ||
            value.Contains('\\') ||
            value.AsSpan().ContainsAny(InvalidFileNameChars))
        {
            throw new ArgumentException($"'{value}' is not a valid single path segment.", paramName);
        }
    }

    /// <summary>
    /// Gets the full path for a database file with the specified name within the writable data folder, ensuring that the directory exists.
    /// </summary>
    /// <param name="dbName"></param>
    /// <returns></returns>
    public static string GetDatabasePath(string dbName)
    {
        var folderPath = EnsureWritableDataFolderPath("db");
        return Path.Combine(folderPath, dbName);
    }

    private static string EnsureDeviceId()
    {
        // Store the device ID in a file within the local application data folder to ensure it persists across sessions on the same machine
        var baseFolderPath = EnsureDirectory(Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "Everywhere"));
        var deviceIdFilePath = Path.Combine(baseFolderPath, ".device_id");

        var fileInfo = new FileInfo(deviceIdFilePath);
        if (fileInfo is { Exists: true, Length: 36 }) // GUID format length
        {
            using var reader = new StreamReader(deviceIdFilePath);
            var existingDeviceId = reader.ReadToEnd().Trim();
            if (Guid.TryParse(existingDeviceId, out _))
            {
                return existingDeviceId;
            }
        }

        // If the file does not exist or is invalid, generate a new device ID, save it to the file, and return it
        var newDeviceId = Guid.CreateVersion7().ToString();
        File.WriteAllText(deviceIdFilePath, newDeviceId);
        return newDeviceId;
    }

    private static string EnsureDirectory(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(path);
        return path;
    }
}
