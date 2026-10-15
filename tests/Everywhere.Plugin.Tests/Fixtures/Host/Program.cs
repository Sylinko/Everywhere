using Everywhere.Plugin.Abstractions;

namespace PluginFixture.Host;

/// <summary>
/// Provides a small plugin-enabled host that uses the production export target.
/// </summary>
public static class HostApi
{
    /// <summary>
    /// Gets a non-constant value so consuming it produces a real host implementation reference.
    /// </summary>
    public static string Name => typeof(IPlugin).FullName ?? "IPlugin";

    /// <summary>
    /// Keeps the fixture executable independently runnable.
    /// </summary>
    public static void Main() => Console.WriteLine(Name);
}
