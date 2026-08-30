namespace Everywhere.Automation.Testing;

public static partial class CommonScenarios
{
    /// <summary>
    /// Gets a terminal application with profiles, sessions, virtual output, split panes, search, and connection state.
    /// </summary>
    public static Scenario Terminal { get; } = Scenario.Define(
        "terminal",
        context =>
        {
            var lineCount = context.RandomInt("line-count", 5_000, 100_000);
            return new Window(
                new MenuBar(
                    new MenuItem("File", new MenuItem("New tab"), new MenuItem("New window"), new Separator(), new MenuItem("Close pane"), new MenuItem("Exit")),
                    new MenuItem("Edit", new MenuItem("Copy"), new MenuItem("Paste"), new MenuItem("Select all"), new MenuItem("Find")),
                    new MenuItem("View", new MenuItem("Command palette"), new MenuItem("Zoom in"), new MenuItem("Reset zoom")),
                    new MenuItem("Terminal", new MenuItem("Split pane"), new MenuItem("Move focus"), new MenuItem("Restart connection")),
                    new MenuItem("Help", new MenuItem("Documentation"), new MenuItem("About"))),
                new ToolBar(
                    new Button("New tab"),
                    new Button("Profiles"),
                    new Button("Split pane"),
                    new Button("Command palette"),
                    new TextBox("Find in terminal") { Key = "terminal-search" },
                    new Button("Previous match"),
                    new Button("Next match"),
                    new Button("Settings")),
                new HorizontalStack(
                    new VerticalStack(
                        new Text("Connections"),
                        new Button("Local PowerShell"),
                        new Button("Developer Command Prompt"),
                        new Button("Ubuntu"),
                        new Button("Build server"),
                        new Separator(),
                        new Text("Recent sessions"),
                        new ListBox(
                            new Button("Everywhere · main"),
                            new Button("Avalonia · diagnostics"),
                            new Button("Server · logs")),
                        new Text("Profiles"),
                        new Link("Manage profiles")),
                    new TabControl(
                        new TabItem(
                            "PowerShell",
                            new HorizontalStack(
                                new VerticalStack(
                                    new Text("PowerShell 7.5 · E:\\Source\\CSharp\\Everywhere"),
                                    new VirtualList(
                                        context,
                                        "lines",
                                        lineCount,
                                        (lineContext, index) => new Text(
                                            $"[{index:D6}] {lineContext.RandomTextValue("content", ScenarioTextKind.Message)}")),
                                    new HorizontalStack(
                                        new Text("PS E:\\Source\\CSharp\\Everywhere>"),
                                        new TextBox(string.Empty) { Key = "prompt", IsCore = true })),
                                new VerticalStack(
                                    new Text("Build output"),
                                    new Document(CreateParagraphs(context, "build-log", 6))
                                    {
                                        States = ScenarioControlStates.ReadOnly,
                                    },
                                    new Text("Process exited with code 0"),
                                    new Text("Press Enter to close this pane")))),
                        new TabItem(
                            "Ubuntu",
                            new Text("Ubuntu 24.04 LTS"),
                            new Document(CreateParagraphs(context, "linux-output", 4))
                            {
                                States = ScenarioControlStates.ReadOnly,
                            },
                            new HorizontalStack(new Text("developer@workstation:~/Everywhere$"), new TextBox(string.Empty))),
                        new TabItem(
                            "Logs",
                            new Text("Application log stream"),
                            new CheckBox("Follow output"),
                            new ComboBox(new Text("All levels"), new Text("Warnings"), new Text("Errors")),
                            new Document(CreateParagraphs(context, "application-logs", 10))
                            {
                                States = ScenarioControlStates.ReadOnly,
                            })),
                    new VerticalStack(
                        new Text("Session inspector"),
                        new Group(
                            new Text("Process"),
                            new Text("pwsh.exe"),
                            new Text("PID 18420"),
                            new Text("Working directory"),
                            new Link("E:\\Source\\CSharp\\Everywhere")),
                        new Group(
                            new Text("Environment"),
                            new Text("Shell: PowerShell 7.5"),
                            new Text("Encoding: UTF-8"),
                            new Text("Dimensions: 148 × 42")),
                        new Button("Export output"),
                        new Button("Terminate process"))),
                new StatusBar(
                    new Text("Connected"),
                    new Text("PowerShell"),
                    new Text("UTF-8"),
                    new Text("148 × 42"),
                    new Text("No tasks running")));
        });
}
