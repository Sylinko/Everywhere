namespace Everywhere.Automation.Testing;

/// <summary>
/// Provides reusable hand-written application prototypes with generated details.
/// </summary>
public static partial class CommonScenarios
{
    /// <summary>
    /// Gets every common application-shape scenario in stable catalog order.
    /// </summary>
    public static IReadOnlyList<Scenario> All =>
    [
        Chat,
        Feed,
        BrowserArticleAndForm,
        Ide,
        FileExplorer,
        Settings,
        Spreadsheet,
        DocumentEditor,
        Terminal,
        Navigation,
    ];

    private static string CreateParagraphs(ScenarioContext context, string key, int count)
    {
        var paragraphs = new string[count];
        for (var i = 0; i < paragraphs.Length; i++)
        {
            paragraphs[i] = context.RandomTextValue($"{key}/{i}", ScenarioTextKind.Paragraph);
        }

        return string.Join(Environment.NewLine + Environment.NewLine, paragraphs);
    }
}
