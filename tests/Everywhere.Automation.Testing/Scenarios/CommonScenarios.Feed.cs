namespace Everywhere.Automation.Testing;

public static partial class CommonScenarios
{
    /// <summary>
    /// Gets a social feed application with navigation, post creation, virtualized history, and discovery panels.
    /// </summary>
    public static Scenario Feed { get; } = Scenario.Define(
        "feed",
        context =>
        {
            var postCount = context.RandomInt("post-count", 250, 2_500);
            return new Window(
                new ToolBar(
                    new Image("Application logo"),
                    new Button("Home"),
                    new Button("Explore"),
                    new Button("Communities"),
                    new TextBox("Search posts, people, and topics") { Key = "search" },
                    new Button("Messages"),
                    new Button("Notifications"),
                    new Button("Create"),
                    new Button("Profile")),
                new HorizontalStack(
                    new VerticalStack(
                        new HorizontalStack(
                            new Image("Profile picture"),
                            new VerticalStack(
                                context.RandomText("profile-name", ScenarioTextKind.UserName),
                                new Text("@sample-user"))),
                        new Button("Home feed"),
                        new Button("Following"),
                        new Button("Saved posts"),
                        new Button("Drafts"),
                        new Text("Your communities"),
                        new ListBox(
                            new Button("Interface engineering"),
                            new Button("Accessibility"),
                            new Button("Desktop applications")),
                        new Link("Show more"),
                        new Text("© Example Social · Privacy · Terms")),
                    new VerticalStack(
                        new Group(
                            new HorizontalStack(
                                new Image("Profile picture"),
                                new TextBox("Share an update with your network") { Key = "new-post" }),
                            new HorizontalStack(
                                new Button("Photo"),
                                new Button("Video"),
                                new Button("Event"),
                                new Button("Write article"),
                                new Button("Publish") { IsCore = true })),
                        new HorizontalStack(
                            new Text("Sort by"),
                            new ComboBox(new Text("Relevant"), new Text("Latest"), new Text("Following"))),
                        new VirtualList(
                            context,
                            "posts",
                            postCount,
                            (postContext, index) => new VerticalStack(
                                new HorizontalStack(
                                    new Image("Author avatar"),
                                    new VerticalStack(
                                        postContext.RandomText("author", ScenarioTextKind.UserName),
                                        new Text($"{index % 23 + 1}h · Public")),
                                    new Button("Post options")),
                                postContext.RandomText("body", ScenarioTextKind.Paragraph),
                                index % 3 == 0 ? new Image("Post attachment") : new Text(string.Empty),
                                new HorizontalStack(
                                    new Text($"{index % 97 + 3} reactions"),
                                    new Text($"{index % 19} comments"),
                                    new Text($"{index % 11} shares")),
                                new HorizontalStack(
                                    new Button("Like") { Key = $"like-{index}" },
                                    new Button("Comment") { Key = $"comment-{index}" },
                                    new Button("Repost"),
                                    new Button("Send")))),
                    new VerticalStack(
                        new Group(
                            new Text("Trending today"),
                            context.RandomText("trend-1", ScenarioTextKind.Title),
                            new Text("4,820 posts"),
                            context.RandomText("trend-2", ScenarioTextKind.Title),
                            new Text("2,103 posts"),
                            new Link("Show all trends")),
                        new Group(
                            new Text("People you may know"),
                            new Repeat(
                                context,
                                "suggestions",
                                4,
                                (personContext, _) => new HorizontalStack(
                                    new Image("Suggested profile"),
                                    personContext.RandomText("name", ScenarioTextKind.UserName),
                                    new Button("Follow"))),
                            new Link("View more suggestions")),
                        new Group(
                            new Text("Upcoming event"),
                            context.RandomText("event-title", ScenarioTextKind.Title),
                            new Text("Tomorrow · 10:00"),
                            new Button("View event")))),
                new StatusBar(new Text("Feed updated just now"), new Button("Load new posts"))));
        });
}
