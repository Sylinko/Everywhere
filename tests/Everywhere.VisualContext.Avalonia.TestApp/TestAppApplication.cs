using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Simple;
using Everywhere.VisualContext.TestApp;

namespace Everywhere.VisualContext.Avalonia.TestApp;

internal sealed class TestAppApplication : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var options = TestAppOptions.Parse(Program.Arguments);
            var controller = new AvaloniaScenarioController(desktop, options);
            desktop.MainWindow = controller.MainWindow;
            controller.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
