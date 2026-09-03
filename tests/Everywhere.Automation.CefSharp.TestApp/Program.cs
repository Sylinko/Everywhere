using CefSharp;
using CefSharp.WinForms;
using Everywhere.Automation.TestApp;

namespace Everywhere.Automation.CefSharp.TestApp;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var (options, externalAddress) = ParseOptions(args);
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
            using var context = new CefSharpScenarioApplicationContext(options, externalAddress);
            Application.Run(context);
            return 0;
        }
        finally
        {
            Cef.Shutdown();
        }
    }

    private static (TestAppOptions Options, string? ExternalAddress) ParseOptions(params IReadOnlyList<string> arguments)
    {
        var scenarioArguments = new List<string>(arguments.Count);
        string? externalAddress = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] != "--url")
            {
                scenarioArguments.Add(arguments[index]);
                continue;
            }

            if (++index >= arguments.Count || !Uri.TryCreate(arguments[index], UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("The CefSharp --url argument must contain an absolute HTTP or HTTPS address.", nameof(arguments));
            }

            externalAddress = uri.AbsoluteUri;
        }

        return (TestAppOptions.Parse(scenarioArguments), externalAddress);
    }
}
