using CefSharp;
using CefSharp.WinForms;
using Everywhere.VisualContext.TestApp;

namespace Everywhere.VisualContext.CefSharp.TestApp;

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
