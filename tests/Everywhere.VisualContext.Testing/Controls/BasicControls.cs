namespace Everywhere.VisualContext.Testing;

/// <summary>
/// Declares a top-level application window.
/// </summary>
public sealed class Window(params IReadOnlyList<VisualControl> children) : FixedContainerControl(children)
{
    public override ScenarioControlKind Kind => ScenarioControlKind.Window;
}

/// <summary>
/// Declares a generic structural container.
/// </summary>
public sealed class Panel(params IReadOnlyList<VisualControl> children) : FixedContainerControl(children)
{
    public override ScenarioControlKind Kind => ScenarioControlKind.Panel;
}

/// <summary>
/// Declares children arranged vertically.
/// </summary>
public sealed class VerticalStack(params IReadOnlyList<VisualControl> children) : FixedContainerControl(children)
{
    public override ScenarioControlKind Kind => ScenarioControlKind.VerticalStack;
}

/// <summary>
/// Declares children arranged horizontally.
/// </summary>
public sealed class HorizontalStack(params IReadOnlyList<VisualControl> children) : FixedContainerControl(children)
{
    public override ScenarioControlKind Kind => ScenarioControlKind.HorizontalStack;
}

/// <summary>
/// Declares a semantically related group of controls.
/// </summary>
public sealed class Group(params IReadOnlyList<VisualControl> children) : FixedContainerControl(children)
{
    public override ScenarioControlKind Kind => ScenarioControlKind.Group;
}

/// <summary>
/// Declares a non-interactive text control.
/// </summary>
public sealed class Text : VisualControl
{
    public override ScenarioControlKind Kind => ScenarioControlKind.Text;

    /// <summary>
    /// Initializes a text control with its full logical content.
    /// </summary>
    public Text(string content)
    {
        TextContent = content;
    }
}

/// <summary>
/// Declares an invokable button.
/// </summary>
public sealed class Button : VisualControl
{
    public override ScenarioControlKind Kind => ScenarioControlKind.Button;

    /// <summary>
    /// Initializes a button with its accessible name.
    /// </summary>
    public Button(string name)
    {
        Name = name;
    }
}

/// <summary>
/// Declares an invokable hyperlink.
/// </summary>
public sealed class Link : VisualControl
{
    public override ScenarioControlKind Kind => ScenarioControlKind.Link;

    /// <summary>
    /// Initializes a hyperlink with its accessible name.
    /// </summary>
    public Link(string name)
    {
        Name = name;
    }
}

/// <summary>
/// Declares an editable text control.
/// </summary>
public sealed class TextBox : VisualControl
{
    public override ScenarioControlKind Kind => ScenarioControlKind.TextBox;

    /// <summary>
    /// Initializes an editable text control with its current value.
    /// </summary>
    public TextBox(string text)
    {
        TextContent = text;
    }
}

/// <summary>
/// Declares a document-like control that may expose both bounded text content and structured children.
/// </summary>
public sealed class Document : FixedContainerControl
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.Document;

    /// <summary>
    /// Initializes a document with its logical text and optional structured children.
    /// </summary>
    public Document(string content, params IReadOnlyList<VisualControl> children) : base(children) => TextContent = content;
}

/// <summary>
/// Declares an image with an accessible name.
/// </summary>
public sealed class Image : VisualControl
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.Image;

    /// <summary>
    /// Initializes an image with its accessible name.
    /// </summary>
    public Image(string name) => Name = name;
}

/// <summary>
/// Declares a check box.
/// </summary>
public sealed class CheckBox : VisualControl
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.CheckBox;

    /// <summary>
    /// Initializes a check box with its accessible name.
    /// </summary>
    public CheckBox(string name) => Name = name;
}

/// <summary>
/// Declares a radio button.
/// </summary>
public sealed class RadioButton : VisualControl
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.RadioButton;

    /// <summary>
    /// Initializes a radio button with its accessible name.
    /// </summary>
    public RadioButton(string name) => Name = name;
}

/// <summary>
/// Declares a combo box and its logical option children.
/// </summary>
public sealed class ComboBox(params IReadOnlyList<VisualControl> children) : FixedContainerControl(children)
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.ComboBox;
}

/// <summary>
/// Declares a slider.
/// </summary>
public sealed class Slider : VisualControl
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.Slider;

    /// <summary>
    /// Initializes a slider with its accessible name.
    /// </summary>
    public Slider(string name) => Name = name;
}

/// <summary>
/// Declares a progress indicator with a normalized percentage value.
/// </summary>
public sealed class ProgressBar : VisualControl
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.ProgressBar;

    /// <summary>
    /// Gets the progress value in the inclusive range from zero through one hundred.
    /// </summary>
    public int Value { get; }

    /// <summary>
    /// Initializes a progress indicator with its accessible name and percentage value.
    /// </summary>
    public ProgressBar(string name, int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);

        Name = name;
        Value = value;
    }
}

/// <summary>
/// Declares a finite list container.
/// </summary>
public sealed class ListBox(params IReadOnlyList<VisualControl> children) : FixedContainerControl(children)
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.List;
}

/// <summary>
/// Declares a hierarchical tree container.
/// </summary>
public sealed class Tree(params IReadOnlyList<VisualControl> children) : FixedContainerControl(children)
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.Tree;
}

/// <summary>
/// Declares a table or grid container.
/// </summary>
public sealed class Table(params IReadOnlyList<VisualControl> children) : FixedContainerControl(children)
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.Table;
}

/// <summary>
/// Declares a tab container.
/// </summary>
public sealed class TabControl(params IReadOnlyList<VisualControl> children) : FixedContainerControl(children)
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.TabControl;
}

/// <summary>
/// Declares one named page hosted by a tab control.
/// </summary>
public sealed class TabItem : FixedContainerControl
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.TabItem;

    /// <summary>
    /// Initializes a tab page with its header and content controls.
    /// </summary>
    public TabItem(string name, params IReadOnlyList<VisualControl> children) : base(children) => Name = name;
}

/// <summary>
/// Declares an application menu bar.
/// </summary>
public sealed class MenuBar(params IReadOnlyList<VisualControl> children) : FixedContainerControl(children)
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.MenuBar;
}

/// <summary>
/// Declares a menu command that may contain submenu items and separators.
/// </summary>
public sealed class MenuItem : FixedContainerControl
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.MenuItem;

    /// <summary>
    /// Initializes a menu item with its header and optional submenu entries.
    /// </summary>
    public MenuItem(string name, params IReadOnlyList<VisualControl> children) : base(children) => Name = name;
}

/// <summary>
/// Declares a semantic separator between adjacent commands or content regions.
/// </summary>
public sealed class Separator : VisualControl
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.Separator;
}

/// <summary>
/// Declares a dialog window.
/// </summary>
public sealed class Dialog(params IReadOnlyList<VisualControl> children) : FixedContainerControl(children)
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.Dialog;
}

/// <summary>
/// Declares a toolbar container.
/// </summary>
public sealed class ToolBar(params IReadOnlyList<VisualControl> children) : FixedContainerControl(children)
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.ToolBar;
}

/// <summary>
/// Declares a status-bar container.
/// </summary>
public sealed class StatusBar(params IReadOnlyList<VisualControl> children) : FixedContainerControl(children)
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.StatusBar;
}
