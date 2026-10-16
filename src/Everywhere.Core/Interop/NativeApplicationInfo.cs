namespace Everywhere.Interop;

/// <summary>
/// Identifies an installed application using its platform matching identity and icon source path.
/// </summary>
public sealed record NativeApplicationInfo(string Identity, string DisplayName, string Path);