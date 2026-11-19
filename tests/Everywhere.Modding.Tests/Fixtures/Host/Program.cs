using Everywhere.Modding.Abstractions;

namespace ModFixture.Host;

/// <summary>
/// Provides a small mod-enabled host that uses the production export target.
/// </summary>
public static class HostApi
{
    /// <summary>
    /// Gets a non-constant value so consuming it produces a real host implementation reference.
    /// </summary>
    public static string Name => typeof(IMod).FullName ?? "IMod";

    /// <summary>
    /// Keeps the fixture executable independently runnable.
    /// </summary>
    public static void Main() => Console.WriteLine(Name);
}
