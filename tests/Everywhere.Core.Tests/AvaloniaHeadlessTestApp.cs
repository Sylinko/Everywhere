using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Simple;

namespace Everywhere.Core.Tests;

public static class AvaloniaHeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .AfterSetup(builder =>
            {
                builder.Instance?.Styles.Add(new SimpleTheme());
            })
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
