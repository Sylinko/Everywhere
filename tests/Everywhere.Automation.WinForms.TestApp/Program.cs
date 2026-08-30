using Everywhere.Automation.TestApp;

namespace Everywhere.Automation.WinForms.TestApp;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var options = TestAppOptions.Parse(args);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var context = new WinFormsScenarioApplicationContext(options);
        Application.Run(context);
    }
}
