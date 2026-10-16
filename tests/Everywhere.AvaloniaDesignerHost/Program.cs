using Avalonia;

namespace Everywhere.AvaloniaDesignerHost;

public class Program : Application
{
    public static void Main(string[] args)
    {
        // This is the entry point for the Avalonia Designer Host.
        // It initializes the Avalonia application and runs it.
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Program>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}