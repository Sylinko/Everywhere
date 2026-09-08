using CefSharp;
using CefSharp.WinForms;
using Everywhere.Automation.TestApp;

namespace Everywhere.Automation.CefSharp.TestApp;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = TestAppOptions.Parse(args);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var settings = new CefSettings
        {
            LogSeverity = LogSeverity.Disable,
            WindowlessRenderingEnabled = false,
        };
        // UIA can otherwise observe only the WinForms host until Chromium decides that an accessibility client is active.
        settings.CefCommandLineArgs.Add("force-renderer-accessibility", "1");
        if (!Cef.Initialize(settings))
        {
            return 1;
        }

        try
        {
            using var context = new CefSharpScenarioApplicationContext(options);
            Application.Run(context);
            return 0;
        }
        finally
        {
            Cef.Shutdown();
        }
    }

}
