using Everywhere.VisualContext.TestApp;

namespace Everywhere.VisualContext.WinForms.TestApp;

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
