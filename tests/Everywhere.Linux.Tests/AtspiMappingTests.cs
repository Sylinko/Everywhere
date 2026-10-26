using System.Reflection;
using Everywhere.Automation;
using Everywhere.Linux.Automation;

namespace Everywhere.Linux.Tests;

public sealed class AtspiMappingTests
{
    [TestCase("panel")]
    [TestCase("filler")]
    [TestCase("push button")]
    [TestCase("label")]
    public void NonEditableStructuralNode_MapStates_DoesNotInventReadOnly(string role)
    {
        Assert.That(Map(role, AtspiState.Showing, AtspiState.Visible, AtspiState.Enabled), Is.EqualTo(VisualElementStates.None));
    }

    [Test]
    public void ExplicitReadOnlyValue_MapStates_PreservesReadOnly()
    {
        Assert.That(Map("text", AtspiState.Showing, AtspiState.Visible, AtspiState.Enabled, AtspiState.ReadOnly),
            Is.EqualTo(VisualElementStates.ReadOnly));
    }

    [Test]
    public void FocusedEditablePassword_MapStates_PreservesFocusAndPrivacy()
    {
        Assert.That(Map("password text", AtspiState.Showing, AtspiState.Visible, AtspiState.Sensitive, AtspiState.Editable, AtspiState.Focused),
            Is.EqualTo(VisualElementStates.Password | VisualElementStates.Focused));
    }

    [Test]
    public void HiddenDisabledNode_MapStates_PreservesIndependentStates()
    {
        Assert.That(Map("push button", AtspiState.Selected),
            Is.EqualTo(VisualElementStates.Offscreen | VisualElementStates.Disabled | VisualElementStates.Selected));
    }

    [TestCase("tree", VisualElementType.TreeView)]
    [TestCase("tree item", VisualElementType.TreeViewItem)]
    [TestCase("tree table", VisualElementType.DataGrid)]
    [TestCase("heading", VisualElementType.Header)]
    [TestCase("column header", VisualElementType.HeaderItem)]
    [TestCase("row header", VisualElementType.HeaderItem)]
    [TestCase("table column header", VisualElementType.HeaderItem)]
    [TestCase("table row header", VisualElementType.HeaderItem)]
    [TestCase("progress bar", VisualElementType.ProgressBar)]
    [TestCase("spin button", VisualElementType.Spinner)]
    [TestCase("tool bar", VisualElementType.ToolBar)]
    [TestCase("status bar", VisualElementType.StatusBar)]
    [TestCase("paragraph", VisualElementType.Label)]
    [TestCase("radio menu item", VisualElementType.MenuItem)]
    [TestCase("frame", VisualElementType.Panel)]
    public void NativeRole_MapType_PreservesSharedSemanticType(string role, VisualElementType expected)
    {
        var method = typeof(AtspiVisualElement).GetMethod("TypeFor", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException();
        Assert.That(method.Invoke(null, [role]), Is.EqualTo(expected));
    }

    private static VisualElementStates Map(string role, params AtspiState[] states)
    {
        var method = typeof(AtspiVisualElement).GetMethod("MapStates", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException();
        var hasState = (Func<AtspiState, bool>)states.Contains;
        return (VisualElementStates)(method.Invoke(null, [role, hasState]) ?? throw new InvalidOperationException());
    }
}