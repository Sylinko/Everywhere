using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Simple;

[assembly: AvaloniaTestApplication(typeof(Everywhere.Linux.Tests.AvaloniaHeadlessTestApp))]

namespace Everywhere.Linux.Tests;

/// <summary>
/// Runs Linux focused-input tests with Avalonia routing and focus, without a display server.
/// Native compositor behavior is verified separately in the desktop acceptance tests.
/// </summary>
public static class AvaloniaHeadlessTestApp
{
    /// <summary>
    /// Creates the headless application used by the Avalonia NUnit integration.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<Application>()
            .AfterSetup(builder =>
            {
                builder.Instance?.Styles.Add(new SimpleTheme());
            })
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}