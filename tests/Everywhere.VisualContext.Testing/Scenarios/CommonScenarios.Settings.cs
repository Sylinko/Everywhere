namespace Everywhere.VisualContext.Testing;

public static partial class CommonScenarios
{
    /// <summary>
    /// Gets a complete desktop settings application with category navigation and grouped preference pages.
    /// </summary>
    public static Scenario Settings { get; } = Scenario.Define(
        "settings",
        context => new Window(
            new ToolBar(
                new Button("Back"),
                new Text("Settings"),
                new TextBox("Find a setting") { Key = "settings-search" },
                new Button("Help")),
            new HorizontalStack(
                new VerticalStack(
                    new HorizontalStack(
                        new Image("User account picture"),
                        new VerticalStack(
                            context.RandomText("account", ScenarioTextKind.UserName),
                            new Text("Local account"))),
                    new Button("System"),
                    new Button("Bluetooth & devices"),
                    new Button("Network & internet"),
                    new Button("Personalization"),
                    new Button("Apps"),
                    new Button("Accounts"),
                    new Button("Time & language"),
                    new Button("Gaming"),
                    new Button("Accessibility"),
                    new Button("Privacy & security"),
                    new Button("Update")),
                new VerticalStack(
                    new Text("Personalization"),
                    new Text("Choose how the application looks and behaves on this device."),
                    new Group(
                        new Text("Appearance"),
                        new HorizontalStack(
                            new Image("Current theme preview"),
                            new VerticalStack(
                                new Text("Theme"),
                                new ComboBox(new Text("Use system setting"), new Text("Light"), new Text("Dark")))),
                        new HorizontalStack(
                            new Text("Accent color"),
                            new ComboBox(new Text("Automatic"), new Text("Blue"), new Text("Purple"), new Text("Green"))),
                        new CheckBox("Show accent color on window borders"),
                        new CheckBox("Use transparency effects")),
                    new Group(
                        new Text("Text and layout"),
                        new Text("Text size"),
                        new Slider("Text size"),
                        new Text("The quick brown fox jumps over the lazy dog."),
                        new HorizontalStack(
                            new Text("Display density"),
                            new ComboBox(new Text("Comfortable"), new Text("Compact"), new Text("Spacious"))),
                        new CheckBox("Use animations"),
                        new CheckBox("Always show scroll bars")),
                    new Group(
                        new Text("Notifications"),
                        new CheckBox("Allow notifications"),
                        new CheckBox("Play a sound for notifications"),
                        new CheckBox("Show message previews"),
                        new HorizontalStack(
                            new Text("Quiet hours"),
                            new TextBox("22:00"),
                            new Text("to"),
                            new TextBox("07:00"))),
                    new Group(
                        new Text("Language and region"),
                        new HorizontalStack(
                            new Text("Display language"),
                            new ComboBox(new Text("English"), new Text("简体中文"), new Text("日本語"), new Text("العربية"))),
                        new HorizontalStack(
                            new Text("Regional format"),
                            new ComboBox(new Text("Recommended"), new Text("United States"), new Text("China"))),
                        new Link("Manage language packs")),
                    new Group(
                        new Text("Files and startup"),
                        new CheckBox("Start with the system"),
                        new CheckBox("Restore the previous session"),
                        new Text("Download location"),
                        new HorizontalStack(
                            new TextBox("C:\\Users\\Sample\\Downloads") { Key = "directory" },
                            new Button("Browse"))),
                    new Group(
                        new Text("Privacy"),
                        new CheckBox("Send optional diagnostic data"),
                        new CheckBox("Improve recommendations using activity"),
                        new Link("View privacy statement")),
                    new HorizontalStack(
                        new Button("Reset to defaults"),
                        new Button("Cancel"),
                        new Button("Apply changes") { Key = "save", IsCore = true }))),
            new StatusBar(new Text("Settings are stored on this device"), new Link("Get help"))));
}
