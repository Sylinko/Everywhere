namespace Everywhere.VisualContext.Testing;

public static partial class CommonScenarios
{
    /// <summary>
    /// Gets an IDE workspace with project navigation, editor chrome, diagnostics, outline, and status information.
    /// </summary>
    public static Scenario Ide { get; } = Scenario.Define(
        "ide",
        context =>
        {
            var fileCount = context.RandomInt("file-count", 40, 160);
            var errorCount = context.RandomInt("error-count", 8, 240);
            return new Window(
                new MenuBar(
                    new MenuItem("File",
                        new MenuItem("New text file"),
                        new MenuItem("New window"),
                        new MenuItem("Open folder"),
                        new Separator(),
                        new MenuItem("Save"),
                        new MenuItem("Save all"),
                        new Separator(),
                        new MenuItem("Exit")),
                    new MenuItem("Edit", new MenuItem("Undo"), new MenuItem("Redo"), new Separator(), new MenuItem("Find"), new MenuItem("Replace")),
                    new MenuItem("Selection", new MenuItem("Select all"), new MenuItem("Add cursor above")),
                    new MenuItem("View", new MenuItem("Command palette"), new MenuItem("Explorer"), new MenuItem("Output")),
                    new MenuItem("Go", new MenuItem("Go to file"), new MenuItem("Go to symbol"), new MenuItem("Go to definition")),
                    new MenuItem("Run", new MenuItem("Start debugging"), new MenuItem("Run without debugging"), new MenuItem("Stop")),
                    new MenuItem("Terminal", new MenuItem("New terminal"), new MenuItem("Split terminal")),
                    new MenuItem("Help", new MenuItem("Documentation"), new MenuItem("About"))),
                new ToolBar(
                    new Button("Navigate back"),
                    new Button("Navigate forward"),
                    new TextBox("Everywhere — Search files and commands") { Key = "command-center" },
                    new Button("Start debugging"),
                    new ComboBox(new Text("Everywhere"), new Text("Tests"), new Text("Release")),
                    new Button("Accounts"),
                    new Button("Manage")),
                new HorizontalStack(
                    new VerticalStack(
                        new Button("Explorer"),
                        new Button("Search"),
                        new Button("Source control"),
                        new Button("Run and debug"),
                        new Button("Extensions"),
                        new Button("Testing")),
                    new VerticalStack(
                        new HorizontalStack(new Text("EXPLORER"), new Button("More actions")),
                        new Text("OPEN EDITORS"),
                        new ListBox(
                            new HorizontalStack(new Text("VisualContextBuilder.cs"), new Button("Close")),
                            new HorizontalStack(new Text("Refactor.md"), new Button("Close"))),
                        new Text("EVERYWHERE"),
                        new Tree(
                            new Group(
                                new Group(
                                    new Text("VisualContextBuilder.cs"),
                                    new Text("VisualContextCapturer.cs"),
                                    new Text("VisualContextOptions.cs")) { Name = "VisualContext" },
                                new Group(
                                    new Text("IVisualElement.cs"),
                                    new Text("IVisualElementContext.cs")) { Name = "Interop" }) { Name = "src" },
                            new Group(
                                new Repeat(
                                    context,
                                    "project-files",
                                    fileCount,
                                    (fileContext, _) => fileContext.RandomText("path", ScenarioTextKind.Title))) { Name = "tests" },
                            new Text("Everywhere.slnx"),
                            new Text("Directory.Packages.props")),
                        new HorizontalStack(new Text("OUTLINE"), new Button("Refresh")),
                        new HorizontalStack(new Text("TIMELINE"), new Button("Filter"))),
                    new VerticalStack(
                        new HorizontalStack(
                            new Group(new Text("VisualContextBuilder.cs"), new Button("Close editor")),
                            new Group(new Text("Refactor.md"), new Text("●"), new Button("Close editor")),
                            new Button("Split editor"),
                            new Button("Editor actions")),
                        new HorizontalStack(
                            new Link("src"),
                            new Text("›"),
                            new Link("Everywhere.Core"),
                            new Text("›"),
                            new Link("Chat"),
                            new Text("›"),
                            new Text("VisualContextBuilder.cs")),
                        new HorizontalStack(
                            new Text("1"),
                            new Document(CreateParagraphs(context, "source", 12))
                            {
                                Key = "editor",
                                IsCore = true,
                            },
                            new Image("Editor minimap")),
                        new TabControl(
                            new TabItem(
                                "Problems",
                                new HorizontalStack(
                                    new Text("PROBLEMS"),
                                    new Text(errorCount.ToString()),
                                    new Button("Filter"),
                                    new Button("Collapse all")),
                                new VirtualList(
                                    context,
                                    "errors",
                                    errorCount,
                                    (errorContext, index) => new HorizontalStack(
                                        new Text(index % 3 == 0 ? "Error" : "Warning"),
                                        errorContext.RandomText("message", ScenarioTextKind.Sentence),
                                        new Link($"VisualContextBuilder.cs:{index + 12}")))),
                            new TabItem(
                                "Output",
                                new Text("OUTPUT"),
                                new Document(CreateParagraphs(context, "build-output", 4))
                                {
                                    States = ScenarioControlStates.ReadOnly,
                                }),
                            new TabItem(
                                "Terminal",
                                new Text("TERMINAL"),
                                new Text("PS E:\\Source\\CSharp\\Everywhere> dotnet build"),
                                new TextBox(string.Empty)))),
                    new VerticalStack(
                        new HorizontalStack(new Text("OUTLINE"), new Button("Sort"), new Button("More")),
                        new TextBox("Filter symbols"),
                        new Tree(
                            new Group(
                                new Text("Build"),
                                new Text("CreateNode"),
                                new Text("Traverse")) { Name = "VisualContextBuilder" },
                            new Group(
                                new Text("Current"),
                                new Text("MoveNext"),
                                new Text("Dispose")) { Name = "VisualEnumerator" }),
                        new Text("REFERENCES"),
                        new ListBox(
                            new Link("VisualContextCapturer.cs:48"),
                            new Link("VisualContextService.cs:91"),
                            new Link("ScenarioMockBackend.cs:224")))),
                new StatusBar(
                    new Button("main*"),
                    new Text("0 errors · 3 warnings"),
                    new Text("Ln 128, Col 24"),
                    new Text("Spaces: 4"),
                    new Button("UTF-8"),
                    new Button("CRLF"),
                    new Button("C#"),
                    new Text("Ready")));
        });
}
