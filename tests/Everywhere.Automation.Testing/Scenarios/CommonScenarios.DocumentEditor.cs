namespace Everywhere.Automation.Testing;

public static partial class CommonScenarios
{
    /// <summary>
    /// Gets a document editor with ribbon commands, navigation, an editable document, comments, and review state.
    /// </summary>
    public static Scenario DocumentEditor { get; } = Scenario.Define(
        "document-editor",
        context => new Window(
            new MenuBar(
                new MenuItem("File",
                    new MenuItem("New"),
                    new MenuItem("Open"),
                    new MenuItem("Save"),
                    new MenuItem("Save as"),
                    new Separator(),
                    new MenuItem("Print"),
                    new MenuItem("Properties")),
                new MenuItem("Edit", new MenuItem("Undo"), new MenuItem("Redo"), new MenuItem("Find"), new MenuItem("Replace")),
                new MenuItem("Insert", new MenuItem("Table"), new MenuItem("Picture"), new MenuItem("Link"), new MenuItem("Comment")),
                new MenuItem("Review", new MenuItem("Spelling"), new MenuItem("Track changes"), new MenuItem("Compare")),
                new MenuItem("View", new MenuItem("Read mode"), new MenuItem("Print layout"), new MenuItem("Navigation pane")),
                new MenuItem("Help", new MenuItem("Editor help"), new MenuItem("About"))),
            new HorizontalStack(
                new Button("AutoSave"),
                new Button("Save"),
                new Button("Undo"),
                new Button("Redo"),
                context.RandomText("document-title", ScenarioTextKind.Title),
                new Text("Saved to this device"),
                new TextBox("Search") { Key = "document-search" },
                new Button("Comments"),
                new Button("Editing mode"),
                new Button("Share")),
            new ToolBar(
                new Button("Paste"),
                new Button("Format painter"),
                new Separator(),
                new ComboBox(new Text("Normal"), new Text("Title"), new Text("Heading 1"), new Text("Heading 2")),
                new ComboBox(new Text("Aptos"), new Text("Arial"), new Text("Times New Roman")),
                new ComboBox(new Text("11"), new Text("12"), new Text("14"), new Text("18")),
                new Button("Bold"),
                new Button("Italic"),
                new Button("Underline"),
                new Button("Text color"),
                new Separator(),
                new Button("Bullets"),
                new Button("Numbering"),
                new Button("Align left"),
                new Button("Center"),
                new Button("Line spacing"),
                new Button("Styles")),
            new HorizontalStack(
                new VerticalStack(
                    new Text("Navigation"),
                    new TextBox("Search document"),
                    new TabControl(
                        new TabItem(
                            "Headings",
                            new Tree(
                                new Group(
                                    new Text("Background"),
                                    new Text("Goals")) { Name = "Introduction" },
                                new Group(
                                    new Text("Architecture"),
                                    new Text("Platform boundaries"),
                                    new Text("Rendering")) { Name = "Design" },
                                new Text("Implementation plan"),
                                new Text("Open questions"))),
                        new TabItem(
                            "Pages",
                            new Repeat(
                                context,
                                "page-thumbnails",
                                8,
                                (_, index) => new Image($"Page {index + 1} thumbnail"))))),
                new VerticalStack(
                    new HorizontalStack(
                        new Text("Horizontal ruler"),
                        new Slider("Left indent"),
                        new Slider("Right indent")),
                    new Document(CreateParagraphs(context, "document", 28))
                    {
                        Key = "document",
                        IsCore = true,
                    },
                    new Text("Page break"),
                    new Document(CreateParagraphs(context, "appendix", 8)) { Key = "appendix" }),
                new VerticalStack(
                    new HorizontalStack(new Text("Comments"), new Button("New comment"), new Button("Close pane")),
                    new VirtualList(
                        context,
                        "comments",
                        context.RandomInt("comment-count", 12, 80),
                        (commentContext, index) => new Group(
                            new HorizontalStack(
                                new Image("Comment author"),
                                commentContext.RandomText("author", ScenarioTextKind.UserName),
                                new Text($"{index + 1}h"),
                                new Button("Comment options")),
                            commentContext.RandomText("body", ScenarioTextKind.Message),
                            new HorizontalStack(new Button("Reply"), new Button("Resolve")))),
                    new Group(
                        new Text("Track changes"),
                        new CheckBox("Track changes while editing"),
                        new ComboBox(new Text("All markup"), new Text("Simple markup"), new Text("No markup"))))),
            new StatusBar(
                new Text("Page 1 of 9"),
                new Text("4,826 words"),
                new Button("English (United States)"),
                new Text("Accessibility: Investigate"),
                new Text("Focus"),
                new Button("Print layout"),
                new Slider("Zoom"))));
}
