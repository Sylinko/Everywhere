namespace Everywhere.VisualContext.Testing;

public static partial class CommonScenarios
{
    /// <summary>
    /// Gets a file manager with navigation, folder hierarchy, virtualized details, preview, and file operations.
    /// </summary>
    public static Scenario FileExplorer { get; } = Scenario.Define(
        "file-explorer",
        context =>
        {
            var fileCount = context.RandomInt("file-count", 1_000, 50_000);
            return new Window(
                new MenuBar(
                    new MenuItem("File",
                        new MenuItem("New window"),
                        new MenuItem("Open command prompt"),
                        new Separator(),
                        new MenuItem("Options"),
                        new MenuItem("Close")),
                    new MenuItem("Home", new MenuItem("Pin to Quick access"), new MenuItem("Properties")),
                    new MenuItem("Share", new MenuItem("Copy path"), new MenuItem("Share with")),
                    new MenuItem("View", new MenuItem("Extra large icons"), new MenuItem("Details"), new MenuItem("Show hidden items")),
                    new MenuItem("Help", new MenuItem("Get help"), new MenuItem("About"))),
                new ToolBar(
                    new Button("New"),
                    new Button("Cut"),
                    new Button("Copy"),
                    new Button("Paste"),
                    new Button("Rename"),
                    new Button("Share"),
                    new Button("Delete"),
                    new Button("Sort"),
                    new Button("View"),
                    new Button("More")),
                new HorizontalStack(
                    new Button("Back"),
                    new Button("Forward"),
                    new Button("Up"),
                    new HorizontalStack(
                        new Link("This PC"),
                        new Text("›"),
                        new Link("Local Disk (E:)"),
                        new Text("›"),
                        new Link("Source"),
                        new Text("›"),
                        new Text("Everywhere")),
                    new Button("Refresh"),
                    new TextBox("Search Everywhere") { Key = "file-search" }),
                new HorizontalStack(
                    new VerticalStack(
                        new Tree(
                            new Group(
                                new Text("Desktop"),
                                new Text("Downloads"),
                                new Text("Documents"),
                                new Text("Pictures")) { Name = "Home" },
                            new Group(
                                new Group(
                                    new Text("src"),
                                    new Text("tests"),
                                    new Text("docs"),
                                    new Text("3rd")) { Name = "Everywhere" },
                                new Text("Avalonia"),
                                new Text("Temporary")) { Name = "Local Disk (E:)" },
                            new Group(new Text("Shared"), new Text("Build server")) { Name = "Network" }),
                        new Group(
                            new Text("Storage"),
                            new Text("Local Disk (E:) · 218 GB free of 512 GB"),
                            new ProgressBar("Storage usage", 57))),
                    new VerticalStack(
                        new HorizontalStack(
                            new Text($"{fileCount:N0} items"),
                            new Button("Select"),
                            new Button("Details pane"),
                            new Button("Preview pane")),
                        new Table(
                            new HorizontalStack(
                                new Button("Name"),
                                new Button("Date modified"),
                                new Button("Type"),
                                new Button("Size")),
                            new VirtualList(
                                context,
                                "files",
                                fileCount,
                                (fileContext, index) => new HorizontalStack(
                                    new Image(index % 9 == 0 ? "Folder" : "File"),
                                    fileContext.RandomText("name", ScenarioTextKind.Title),
                                    new Text($"2026-08-{index % 28 + 1:D2} {index % 24:D2}:{index % 60:D2}"),
                                    new Text(index % 9 == 0 ? "File folder" : index % 4 == 0 ? "C# source file" : "Document"),
                                    new Text(index % 9 == 0 ? string.Empty : $"{index % 8192 + 1} KB"),
                                    new Button("Open") { Key = $"open-{index}" })))),
                    new VerticalStack(
                        new Text("Preview"),
                        new Image("Selected file preview"),
                        context.RandomText("selected-name", ScenarioTextKind.Title),
                        new Text("C# source file"),
                        new Text("Modified today at 14:32"),
                        new Text("Size: 18.4 KB"),
                        new Link("Open file location"),
                        new HorizontalStack(new Button("Open"), new Button("Share")),
                        new Text("Properties"),
                        new Group(
                            new Text("Path: E:\\Source\\CSharp\\Everywhere\\src"),
                            new Text("Owner: Developer"),
                            new CheckBox("Read-only"),
                            new CheckBox("Hidden")))),
                new StatusBar(
                    new Text($"{fileCount:N0} items"),
                    new Text("1 item selected · 18.4 KB"),
                    new Button("Details view"),
                    new Slider("Icon size")));
        });
}
