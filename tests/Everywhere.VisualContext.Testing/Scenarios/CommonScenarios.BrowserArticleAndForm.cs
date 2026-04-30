namespace Everywhere.VisualContext.Testing;

public static partial class CommonScenarios
{
    /// <summary>
    /// Gets a browser window containing a long-form article, site navigation, and a complete response form.
    /// </summary>
    public static Scenario BrowserArticleAndForm { get; } = Scenario.Define(
        "browser-article-form",
        context => new Window(
            new MenuBar(
                new MenuItem("File",
                    new MenuItem("New tab"),
                    new MenuItem("New window"),
                    new MenuItem("Open file"),
                    new Separator(),
                    new MenuItem("Print"),
                    new MenuItem("Exit")),
                new MenuItem("Edit", new MenuItem("Cut"), new MenuItem("Copy"), new MenuItem("Paste"), new MenuItem("Find")),
                new MenuItem("View", new MenuItem("Zoom in"), new MenuItem("Zoom out"), new MenuItem("Full screen")),
                new MenuItem("History", new MenuItem("Recently closed"), new MenuItem("Show all history")),
                new MenuItem("Bookmarks", new MenuItem("Bookmark this tab"), new MenuItem("Manage bookmarks")),
                new MenuItem("Help", new MenuItem("Report site issue"), new MenuItem("About browser"))),
            new HorizontalStack(
                new Button("Previous tab"),
                new Group(new Image("Site icon"), context.RandomText("tab-title", ScenarioTextKind.Title), new Button("Close tab")),
                new Button("New tab"),
                new Button("Tab actions")),
            new ToolBar(
                new Button("Back"),
                new Button("Forward"),
                new Button("Reload"),
                new TextBox("https://example.invalid/articles/accessible-desktop-apps") { Key = "address" },
                new Button("Reader mode"),
                new Button("Bookmark"),
                new Button("Extensions"),
                new Button("Browser menu")),
            new HorizontalStack(
                new VerticalStack(
                    new ToolBar(
                        new Image("Publication logo"),
                        new Button("Topics"),
                        new Button("Guides"),
                        new Button("Reviews"),
                        new TextBox("Search this site"),
                        new Button("Subscribe")),
                    new Text("Engineering · Desktop applications"),
                    context.RandomText("article-title", ScenarioTextKind.Title),
                    context.RandomText("article-summary", ScenarioTextKind.Sentence),
                    new HorizontalStack(
                        new Image("Author portrait"),
                        context.RandomText("author", ScenarioTextKind.UserName),
                        new Text("Published today · 12 minute read"),
                        new Button("Follow author")),
                    new HorizontalStack(
                        new Button("Share"),
                        new Button("Save"),
                        new Button("Print"),
                        new Button("Listen")),
                    new Image("Article cover illustration"),
                    new Document(CreateParagraphs(context, "article", 14))
                    {
                        Key = "article",
                        IsCore = true,
                        States = ScenarioControlStates.ReadOnly,
                    },
                    new Text("Was this article useful?"),
                    new HorizontalStack(new Button("Yes"), new Button("No")),
                    new Group(
                        new Text("Leave a response"),
                        new HorizontalStack(
                            new VerticalStack(new Text("Name"), new TextBox(context.RandomTextValue("name", ScenarioTextKind.UserName))),
                            new VerticalStack(new Text("Email"), new TextBox("reader@example.invalid"))),
                        new Text("Topic"),
                        new ComboBox(new Text("General feedback"), new Text("Technical question"), new Text("Correction")),
                        new Text("Response"),
                        new TextBox(context.RandomTextValue("response", ScenarioTextKind.Paragraph)) { Key = "response" },
                        new CheckBox("Email me when someone replies"),
                        new CheckBox("I agree to the community guidelines"),
                        new HorizontalStack(new Button("Preview"), new Button("Submit response")))),
                new VerticalStack(
                    new Group(
                        new Text("On this page"),
                        new Link("Why accessibility trees matter"),
                        new Link("Choosing a safe boundary"),
                        new Link("Handling virtualized content"),
                        new Link("Practical recommendations")),
                    new Group(
                        new Text("Related articles"),
                        new Repeat(
                            context,
                            "related",
                            5,
                            (relatedContext, _) => new VerticalStack(
                                relatedContext.RandomText("title", ScenarioTextKind.Title),
                                relatedContext.RandomText("summary", ScenarioTextKind.Sentence),
                                new Link("Read article")))),
                    new Group(
                        new Text("Newsletter"),
                        new Text("A weekly digest for application engineers."),
                        new TextBox("you@example.invalid"),
                        new Button("Subscribe")))),
            new StatusBar(
                new Text("Done"),
                new Link("https://example.invalid"),
                new Text("100%"),
                new Button("Page zoom"))));
}
