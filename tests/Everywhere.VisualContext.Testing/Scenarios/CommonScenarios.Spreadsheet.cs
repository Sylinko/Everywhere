namespace Everywhere.VisualContext.Testing;

public static partial class CommonScenarios
{
    /// <summary>
    /// Gets a spreadsheet application with workbook commands, formulas, a virtual grid, sheets, and analysis tools.
    /// </summary>
    public static Scenario Spreadsheet { get; } = Scenario.Define(
        "spreadsheet",
        context =>
        {
            var rowCount = context.RandomInt("row-count", 10_000, 100_000);
            return new Window(
                new MenuBar(
                    new MenuItem("File",
                        new MenuItem("New workbook"),
                        new MenuItem("Open"),
                        new MenuItem("Save"),
                        new MenuItem("Save as"),
                        new Separator(),
                        new MenuItem("Print"),
                        new MenuItem("Close")),
                    new MenuItem("Edit", new MenuItem("Undo"), new MenuItem("Redo"), new MenuItem("Cut"), new MenuItem("Copy"), new MenuItem("Paste")),
                    new MenuItem("View", new MenuItem("Freeze panes"), new MenuItem("Gridlines"), new MenuItem("Formula bar")),
                    new MenuItem("Insert", new MenuItem("Cells"), new MenuItem("Chart"), new MenuItem("Pivot table")),
                    new MenuItem("Data", new MenuItem("Sort"), new MenuItem("Filter"), new MenuItem("Refresh all")),
                    new MenuItem("Help", new MenuItem("Training"), new MenuItem("About"))),
                new HorizontalStack(
                    new Button("Save"),
                    new Button("Undo"),
                    new Button("Redo"),
                    new Text("Quarterly forecast.xlsx"),
                    new Text("Saved"),
                    new TextBox("Search commands") { Key = "command-search" },
                    new Button("Comments"),
                    new Button("Share")),
                new ToolBar(
                    new Button("Paste"),
                    new Button("Cut"),
                    new Button("Copy"),
                    new Separator(),
                    new ComboBox(new Text("Calibri"), new Text("Arial"), new Text("Consolas")),
                    new ComboBox(new Text("10"), new Text("11"), new Text("12"), new Text("14")),
                    new Button("Bold"),
                    new Button("Italic"),
                    new Button("Underline"),
                    new Separator(),
                    new Button("Currency"),
                    new Button("Percent"),
                    new Button("Decrease decimal"),
                    new Button("Increase decimal"),
                    new Button("Conditional formatting"),
                    new Button("Sort and filter")),
                new HorizontalStack(
                    new TextBox("C12") { Key = "name-box" },
                    new Button("Insert function"),
                    new TextBox("=SUM(C2:C11)") { Key = "formula", IsCore = true }),
                new HorizontalStack(
                    new VerticalStack(
                        new Table(
                            new HorizontalStack(
                                new Button("#"),
                                new Button("A"),
                                new Button("B"),
                                new Button("C"),
                                new Button("D"),
                                new Button("E"),
                                new Button("F")),
                            new VirtualList(
                                context,
                                "rows",
                                rowCount,
                                (rowContext, rowIndex) => new HorizontalStack(
                                    new Text((rowIndex + 1).ToString()),
                                    rowContext.RandomText("account", ScenarioTextKind.Title),
                                    new Text($"{rowIndex % 97 + 1}"),
                                    new Text($"{(rowIndex * 17) % 10000 / 100.0:F2}"),
                                    new Text($"{(rowIndex * 23) % 100}%"),
                                    rowContext.RandomText("owner", ScenarioTextKind.UserName),
                                    new Text(rowIndex % 5 == 0 ? "Review" : "Approved")))),
                        new HorizontalStack(
                            new Button("Sheet navigation"),
                            new TabControl(
                                new TabItem("Summary", new Text("Summary sheet")),
                                new TabItem("Forecast", new Text("Forecast sheet")),
                                new TabItem("Actuals", new Text("Actuals sheet"))),
                            new Button("New sheet"),
                            new Button("All sheets"))),
                    new VerticalStack(
                        new HorizontalStack(new Text("Format cells"), new Button("Close pane")),
                        new TabControl(
                            new TabItem(
                                "Fill & line",
                                new Text("Fill color"),
                                new ComboBox(new Text("Automatic"), new Text("Blue"), new Text("Green")),
                                new Text("Border"),
                                new ComboBox(new Text("None"), new Text("Outline"), new Text("All borders"))),
                            new TabItem(
                                "Effects",
                                new CheckBox("Shadow"),
                                new CheckBox("Soft edges")),
                            new TabItem(
                                "Size",
                                new Text("Column width"),
                                new TextBox("12.5"),
                                new Text("Row height"),
                                new TextBox("20"))),
                        new Group(
                            new Text("Quick analysis"),
                            new Button("Totals"),
                            new Button("Charts"),
                            new Button("Formatting")))),
                new StatusBar(
                    new Text("Ready"),
                    new Text("Average: 482.17 · Count: 24 · Sum: 11,572.08"),
                    new ProgressBar("Calculation progress", 100),
                    new Button("Normal view"),
                    new Slider("Zoom")));
        });
}
