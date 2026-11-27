using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Simple;
using Everywhere.Views;

namespace Everywhere.Core.Tests;

public static class AvaloniaHeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        var shouldRender = Environment.GetEnvironmentVariable("EVERYWHERE_RENDER_OUTPUT") is not null;
        var builder = AppBuilder.Configure<Application>()
            .AfterSetup(builder =>
            {
                ResetAppearanceManager();
                builder.Instance?.Resources.MergedDictionaries.Add(new AppearanceManager());
                builder.Instance?.Styles.Add(new SimpleTheme());
            })
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = !shouldRender });
        return shouldRender ? builder.UseSkia() : builder;
    }

    internal static void ResetAppearanceManager()
    {
        var field = typeof(AppearanceManager).GetField("_shared", BindingFlags.Static | BindingFlags.NonPublic);
        field?.SetValue(null, null);

        var accentField = typeof(ShadUI.Themes.SystemAccentColors).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        accentField?.SetValue(null, null);
    }
}
