using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Simple;

namespace Everywhere.Core.Tests;

public static class AvaloniaHeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        var shouldRender = Environment.GetEnvironmentVariable("EVERYWHERE_RENDER_OUTPUT") is not null;
        var builder = AppBuilder.Configure<Application>()
            .AfterSetup(builder =>
            {
                builder.Instance?.Styles.Add(new SimpleTheme());
            })
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = !shouldRender });
        return shouldRender ? builder.UseSkia() : builder;
    }
}
