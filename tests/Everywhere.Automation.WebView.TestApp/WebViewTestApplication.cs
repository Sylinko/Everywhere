using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Simple;

namespace Everywhere.Automation.WebView.TestApp;

internal sealed class WebViewTestApplication : Application
{
    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new SimpleTheme());

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var controller = new WebViewTestController(desktop, WebViewTestOptions.Parse(Program.Arguments));
            desktop.MainWindow = controller.MainWindow;
            controller.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
