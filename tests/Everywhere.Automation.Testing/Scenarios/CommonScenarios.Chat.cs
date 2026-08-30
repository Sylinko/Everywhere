namespace Everywhere.Automation.Testing;

public static partial class CommonScenarios
{
    /// <summary>
    /// Gets a desktop chat application with conversation navigation, message history, details, and a composer.
    /// </summary>
    public static Scenario Chat { get; } = Scenario.Define(
        "chat",
        context =>
        {
            var conversationCount = context.RandomInt("conversation-count", 24, 96);
            var messageCount = context.RandomInt("message-count", 40, 180);
            return new Window(
                new MenuBar(
                    new MenuItem("Chat",
                        new MenuItem("New conversation"),
                        new MenuItem("New group"),
                        new Separator(),
                        new MenuItem("Sign out"),
                        new MenuItem("Exit")),
                    new MenuItem("Edit", new MenuItem("Undo"), new MenuItem("Copy"), new MenuItem("Paste")),
                    new MenuItem("View", new MenuItem("Compact mode"), new MenuItem("Zoom")),
                    new MenuItem("Conversation",
                        new MenuItem("Search messages"),
                        new MenuItem("Mute notifications"),
                        new MenuItem("Conversation details")),
                    new MenuItem("Help", new MenuItem("Keyboard shortcuts"), new MenuItem("About"))),
                new ToolBar(
                    new Button("Back"),
                    new Button("Forward"),
                    new TextBox("Search people, conversations, and messages") { Key = "global-search" },
                    new Button("New conversation"),
                    new Button("Notifications"),
                    new Button("Account")),
                new HorizontalStack(
                    new VerticalStack(
                        new HorizontalStack(
                            new Image("Signed-in account avatar"),
                            new VerticalStack(
                                context.RandomText("account-name", ScenarioTextKind.UserName),
                                new Text("Available · Notifications enabled"))),
                        new HorizontalStack(
                            new Button("Chats"),
                            new Button("Calls"),
                            new Button("Contacts")),
                        new TextBox("Filter conversations") { Key = "conversation-filter" },
                        new Text("Pinned"),
                        new ListBox(
                            new HorizontalStack(
                                new Image("Team avatar"),
                                new VerticalStack(new Text("Product team"), new Text("Design review at 15:00")),
                                new Text("2")),
                            new HorizontalStack(
                                new Image("Project avatar"),
                                new VerticalStack(new Text("Everywhere"), new Text("Build completed successfully")))),
                        new Text("Recent conversations"),
                        new VirtualList(
                            context,
                            "conversations",
                            conversationCount,
                            (itemContext, index) => new HorizontalStack(
                                new Image("Conversation avatar"),
                                new VerticalStack(
                                    itemContext.RandomText("title", ScenarioTextKind.UserName),
                                    itemContext.RandomText("preview", ScenarioTextKind.Message)),
                                new VerticalStack(
                                    new Text($"{8 + index % 12}:{index % 60:D2}"),
                                    index % 7 == 0 ? new Text("Muted") : new Text(string.Empty))))),
                    new VerticalStack(
                        new HorizontalStack(
                            new Image("Current conversation avatar"),
                            new VerticalStack(
                                context.RandomText("conversation-title", ScenarioTextKind.Title),
                                new Text("12 members · 4 online")),
                            new Button("Start audio call"),
                            new Button("Start video call"),
                            new Button("Search in conversation"),
                            new Button("More options")),
                        new Text("Today"),
                        new VirtualList(
                            context,
                            "messages",
                            messageCount,
                            (itemContext, index) => new VerticalStack(
                                new HorizontalStack(
                                    new Image("Sender avatar"),
                                    itemContext.RandomText("sender", ScenarioTextKind.UserName),
                                    new Text($"{9 + index % 10}:{index % 60:D2}")),
                                new Text(itemContext.RandomTextValue("body", ScenarioTextKind.Message)) { Key = "body" },
                                new HorizontalStack(
                                    new Button("React") { Key = $"react-{index}" },
                                    new Button("Reply") { Key = $"reply-{index}" },
                                    new Button("More")))),
                        new Text("Someone is typing…"),
                        new HorizontalStack(
                            new Button("Attach file"),
                            new Button("Insert image"),
                            new TextBox(context.RandomTextValue("draft", ScenarioTextKind.Message))
                            {
                                Key = "input",
                                IsCore = true,
                            },
                            new Button("Emoji"),
                            new Button("Send"))),
                    new VerticalStack(
                        new Text("Conversation details"),
                        new Image("Conversation image"),
                        context.RandomText("details-title", ScenarioTextKind.Title),
                        new Link("View all members"),
                        new Button("Shared media"),
                        new Button("Shared files"),
                        new Button("Pinned messages"),
                        new CheckBox("Mute notifications"),
                        new Button("Leave conversation"))),
                new StatusBar(
                    new Text("Connected"),
                    new Text("Messages are synchronized"),
                    new Button("Accessibility options")));
        });
}
