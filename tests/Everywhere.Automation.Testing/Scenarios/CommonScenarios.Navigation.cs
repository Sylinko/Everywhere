namespace Everywhere.Automation.Testing;

public static partial class CommonScenarios
{
    /// <summary>
    /// Gets a project-management application emphasizing menus, navigation, tabs, data views, and a modal workflow.
    /// </summary>
    public static Scenario Navigation { get; } = Scenario.Define(
        "navigation",
        context => new Window(
            new MenuBar(
                new MenuItem("Workspace", new MenuItem("Switch workspace"), new MenuItem("Workspace settings"), new Separator(), new MenuItem("Sign out")),
                new MenuItem("Project", new MenuItem("New project"), new MenuItem("Import"), new MenuItem("Archive")),
                new MenuItem("Item", new MenuItem("New task"), new MenuItem("Duplicate"), new MenuItem("Move"), new MenuItem("Delete")),
                new MenuItem("View", new MenuItem("Dashboard"), new MenuItem("Board"), new MenuItem("Timeline"), new MenuItem("Reports")),
                new MenuItem("Help", new MenuItem("Keyboard shortcuts"), new MenuItem("Contact support"), new MenuItem("About"))),
            new ToolBar(
                new Image("Workspace logo"),
                new Button("Workspace switcher"),
                new TextBox("Search projects, tasks, and people") { Key = "workspace-search" },
                new Button("Quick create"),
                new Button("Inbox"),
                new Button("Notifications"),
                new Button("Profile")),
            new HorizontalStack(
                new VerticalStack(
                    new Button("Home"),
                    new Button("My tasks"),
                    new Button("Inbox"),
                    new Text("Favorites"),
                    new ListBox(
                        new Button("Everywhere launch"),
                        new Button("Desktop quality"),
                        new Button("Accessibility research")),
                    new Text("Teams"),
                    new Tree(
                        new Group(new Text("Roadmap"), new Text("Current sprint"), new Text("Backlog")) { Name = "Engineering" },
                        new Group(new Text("Research"), new Text("Design system")) { Name = "Design" },
                        new Group(new Text("Launch plan"), new Text("Announcements")) { Name = "Marketing" }),
                    new Button("Invite people")),
                new VerticalStack(
                    new HorizontalStack(
                        new VerticalStack(
                            context.RandomText("project-title", ScenarioTextKind.Title),
                            new Text("Engineering · Updated 4 minutes ago")),
                        new Button("Star project"),
                        new Button("Project members"),
                        new Button("Share"),
                        new Button("Project actions")),
                    new TabControl(
                        new TabItem(
                            "Overview",
                            new HorizontalStack(
                                new Group(
                                    new Text("Sprint progress"),
                                    new ProgressBar("Sprint completion", 68),
                                    new Text("34 of 50 tasks completed")),
                                new Group(
                                    new Text("Due this week"),
                                    new Text("8 tasks"),
                                    new Link("View tasks")),
                                new Group(
                                    new Text("Team workload"),
                                    new Text("6 members active"),
                                    new Link("Open workload"))),
                            new Text("Recent activity"),
                            new VirtualList(
                                context,
                                "activity",
                                120,
                                (activityContext, index) => new HorizontalStack(
                                    new Image("Activity author"),
                                    activityContext.RandomText("actor", ScenarioTextKind.UserName),
                                    activityContext.RandomText("action", ScenarioTextKind.Message),
                                    new Text($"{index + 1}m")))),
                        new TabItem(
                            "Board",
                            new HorizontalStack(
                                CreateTaskColumn(context.For("backlog"), "Backlog", 18),
                                CreateTaskColumn(context.For("progress"), "In progress", 9),
                                CreateTaskColumn(context.For("review"), "Review", 6),
                                CreateTaskColumn(context.For("done"), "Done", 34))),
                        new TabItem(
                            "List",
                            new Table(
                                new HorizontalStack(new Button("Task"), new Button("Assignee"), new Button("Status"), new Button("Due date")),
                                new VirtualList(
                                    context,
                                    "tasks",
                                    500,
                                    (taskContext, index) => new HorizontalStack(
                                        new CheckBox($"Select task {index + 1}"),
                                        taskContext.RandomText("title", ScenarioTextKind.Title),
                                        taskContext.RandomText("assignee", ScenarioTextKind.UserName),
                                        new Text(index % 3 == 0 ? "In progress" : "Backlog"),
                                        new Text($"Sep {index % 28 + 1}"),
                                        new Button("Open task"))))),
                        new TabItem(
                            "Files",
                            new ToolBar(new Button("Upload"), new Button("New folder"), new Button("Sort")),
                            new ListBox(
                                new HorizontalStack(new Image("Document"), new Text("Visual context specification"), new Button("Open")),
                                new HorizontalStack(new Image("Spreadsheet"), new Text("Launch checklist"), new Button("Open")),
                                new HorizontalStack(new Image("Image"), new Text("Architecture diagram"), new Button("Open"))))),
                    new Dialog(
                        new Text("Create a new task"),
                        new Text("Task name"),
                        new TextBox(context.RandomTextValue("new-task-title", ScenarioTextKind.Title)) { Key = "task-name" },
                        new Text("Description"),
                        new TextBox(context.RandomTextValue("new-task-description", ScenarioTextKind.Paragraph)),
                        new HorizontalStack(
                            new VerticalStack(
                                new Text("Assignee"),
                                new ComboBox(
                                    context.RandomText("assignee-1", ScenarioTextKind.UserName),
                                    context.RandomText("assignee-2", ScenarioTextKind.UserName))),
                            new VerticalStack(
                                new Text("Priority"),
                                new ComboBox(new Text("Low"), new Text("Medium"), new Text("High")))),
                        new CheckBox("Add to current sprint"),
                        new HorizontalStack(
                            new Button("Cancel"),
                            new Button("Create task") { Key = "create-task", IsCore = true }))),
                new VerticalStack(
                    new Text("Project details"),
                    new Text("Owner"),
                    context.RandomText("owner", ScenarioTextKind.UserName),
                    new Text("Members"),
                    new Repeat(
                        context,
                        "members",
                        6,
                        (memberContext, _) => new HorizontalStack(
                            new Image("Member avatar"),
                            memberContext.RandomText("name", ScenarioTextKind.UserName))),
                    new Link("Manage members"),
                    new Separator(),
                    new Text("Milestones"),
                    new CheckBox("Prototype complete"),
                    new CheckBox("Platform integration"),
                    new CheckBox("Release candidate"),
                    new Button("Add milestone"))),
            new StatusBar(
                new Text("All changes saved"),
                new Text("12 collaborators online"),
                new Button("Sync now"),
                new Link("Service status"))));

    private static Group CreateTaskColumn(ScenarioContext context, string name, int count) =>
        new(
            new HorizontalStack(new Text(name), new Text(count.ToString()), new Button("Column options")),
            new Repeat(
                context,
                "cards",
                Math.Min(count, 8),
                (cardContext, index) => new Group(
                    cardContext.RandomText("title", ScenarioTextKind.Title),
                    new Text(index % 2 == 0 ? "High priority" : "Normal priority"),
                    new HorizontalStack(new Image("Assignee"), new Text($"{index % 5 + 1} comments")))),
            new Button("Add task"));
}
