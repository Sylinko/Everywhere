using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Simple;

[assembly: AvaloniaTestApplication(typeof(Everywhere.Modding.Tests.AvaloniaHeadlessTestApp))]

namespace Everywhere.Modding.Tests;

/// <summary>
/// Configures the headless Avalonia application for modding lifecycle UI-thread tests.
/// </summary>
public static class AvaloniaHeadlessTestApp
{
    /// <summary>
    /// Creates the headless application used by Avalonia NUnit tests.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<Application>()
            .AfterSetup(builder =>
            {
                builder.Instance?.Styles.Add(new SimpleTheme());
            })
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
    }
}
