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
            .WithInterFont()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = Environment.GetEnvironmentVariable("EVERYWHERE_UPDATE_PREVIEW_DIR") is null
            });
}
