using Avalonia;

namespace Everywhere.Automation.Avalonia.TestApp;

internal static class Program
{
    internal static IReadOnlyList<string> Arguments { get; private set; } = [];

    [STAThread]
    private static int Main(string[] args)
    {
        Arguments = args;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestAppApplication>().UsePlatformDetect();
}
