using Everywhere.Automation;
using Everywhere.Windows.Interop.UIAutomation;

namespace Everywhere.Windows.Extensions;

/// <summary>
/// Maps native Windows UI Automation metadata to the platform-independent Automation model.
/// </summary>
public static class UIAutomationExtensions
{
    /// <summary>
    /// Maps a native UI Automation control type to a platform-independent visual element type.
    /// </summary>
    /// <param name="controlType">The native UI Automation control type.</param>
    /// <param name="isTopLevelWindow">Whether the element owns a top-level native window.</param>
    /// <returns>The normalized visual element type.</returns>
    public static VisualElementType ToVisualElementType(this UIAutomationControlType controlType, bool isTopLevelWindow) =>
        controlType switch
        {
            UIAutomationControlType.AppBar => VisualElementType.Menu,
            UIAutomationControlType.Button => VisualElementType.Button,
            UIAutomationControlType.Calendar => VisualElementType.Label,
            UIAutomationControlType.CheckBox => VisualElementType.CheckBox,
            UIAutomationControlType.ComboBox => VisualElementType.ComboBox,
            UIAutomationControlType.DataGrid => VisualElementType.DataGrid,
            UIAutomationControlType.DataItem => VisualElementType.DataGridItem,
            UIAutomationControlType.Document => VisualElementType.Document,
            UIAutomationControlType.Edit => VisualElementType.TextEdit,
            UIAutomationControlType.Group => VisualElementType.Panel,
            UIAutomationControlType.Header or UIAutomationControlType.HeaderItem => VisualElementType.TableRow,
            UIAutomationControlType.Hyperlink => VisualElementType.Hyperlink,
            UIAutomationControlType.Image => VisualElementType.Image,
            UIAutomationControlType.List => VisualElementType.ListView,
            UIAutomationControlType.ListItem => VisualElementType.ListViewItem,
            UIAutomationControlType.Menu or UIAutomationControlType.MenuBar => VisualElementType.Menu,
            UIAutomationControlType.MenuItem => VisualElementType.MenuItem,
            UIAutomationControlType.Pane when isTopLevelWindow => VisualElementType.TopLevel,
            UIAutomationControlType.Pane => VisualElementType.Panel,
            UIAutomationControlType.ProgressBar => VisualElementType.ProgressBar,
            UIAutomationControlType.RadioButton => VisualElementType.RadioButton,
            UIAutomationControlType.ScrollBar => VisualElementType.ScrollBar,
            UIAutomationControlType.SemanticZoom => VisualElementType.ListView,
            UIAutomationControlType.Separator => VisualElementType.Unknown,
            UIAutomationControlType.Slider or UIAutomationControlType.Spinner => VisualElementType.Slider,
            UIAutomationControlType.SplitButton => VisualElementType.Button,
            UIAutomationControlType.StatusBar => VisualElementType.Panel,
            UIAutomationControlType.Tab => VisualElementType.TabControl,
            UIAutomationControlType.TabItem => VisualElementType.TabItem,
            UIAutomationControlType.Table => VisualElementType.Table,
            UIAutomationControlType.Text => VisualElementType.Label,
            UIAutomationControlType.Thumb => VisualElementType.Slider,
            UIAutomationControlType.TitleBar or UIAutomationControlType.ToolBar or UIAutomationControlType.ToolTip => VisualElementType.Panel,
            UIAutomationControlType.Tree => VisualElementType.TreeView,
            UIAutomationControlType.TreeItem => VisualElementType.TreeViewItem,
            UIAutomationControlType.Window when isTopLevelWindow => VisualElementType.TopLevel,
            UIAutomationControlType.Window => VisualElementType.Panel,
            _ => VisualElementType.Unknown,
        };
}
