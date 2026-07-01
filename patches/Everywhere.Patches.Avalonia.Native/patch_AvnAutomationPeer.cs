#nullable disable
#if MACOS

// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global
using Avalonia.Native.Interop;
using MonoMod;

namespace Everywhere.Patches.Avalonia.Native;

[MonoModPatch("Avalonia.Native.AvnAutomationPeer")]
internal class patch_AvnAutomationPeer : IAvnAutomationPeer
{
    [MonoModReplace]
    public void SetNode(IAvnAutomationNode peer)
    {
        // Skip original method
    }

    [MonoModIgnore]
    public int HasKeyboardFocus()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsContentElement()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsControlElement()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsEnabled()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsKeyboardFocusable()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void SetFocus()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int ShowContextMenu()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void BringIntoView()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsInteropPeer()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IntPtr InteropPeer_GetNativeControlHandle()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsRootProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnWindowBase RootProvider_GetWindow()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnAutomationPeer RootProvider_GetFocus()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnAutomationPeer RootProvider_GetPeerFromPoint(AvnPoint point)
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsEmbeddedRootProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnAutomationPeer EmbeddedRootProvider_GetFocus()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnAutomationPeer EmbeddedRootProvider_GetPeerFromPoint(AvnPoint point)
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsExpandCollapseProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int ExpandCollapseProvider_GetIsExpanded()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int ExpandCollapseProvider_GetShowsMenu()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void ExpandCollapseProvider_Expand()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void ExpandCollapseProvider_Collapse()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsInvokeProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void InvokeProvider_Invoke()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsRangeValueProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public double RangeValueProvider_GetValue()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public double RangeValueProvider_GetMinimum()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public double RangeValueProvider_GetMaximum()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public double RangeValueProvider_GetSmallChange()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public double RangeValueProvider_GetLargeChange()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void RangeValueProvider_SetValue(double value)
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int RangeValueProvider_IsReadOnly()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsSelectionItemProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int SelectionItemProvider_IsSelected()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void SelectionItemProvider_Select()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void SelectionItemProvider_AddToSelection()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void SelectionItemProvider_RemoveFromSelection()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnAutomationPeer ScrollProvider_GetHorizontalScrollBar()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnAutomationPeer ScrollProvider_GetVerticalScrollBar()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsToggleProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int ToggleProvider_GetToggleState()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void ToggleProvider_Toggle()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int IsValueProvider()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public IAvnString ValueProvider_GetValue()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public void ValueProvider_SetValue(string value)
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore]
    public int ValueProvider_IsReadOnly()
    {
        throw new NotSupportedException();
    }

    [MonoModIgnore] public IAvnAutomationNode Node => throw new NotSupportedException();
    [MonoModIgnore] public IAvnString AcceleratorKey => throw new NotSupportedException();
    [MonoModIgnore] public IAvnString AccessKey => throw new NotSupportedException();
    [MonoModIgnore] public AvnAutomationControlType AutomationControlType => throw new NotSupportedException();
    [MonoModIgnore] public IAvnString AutomationId => throw new NotSupportedException();
    [MonoModIgnore] public AvnRect BoundingRectangle => throw new NotSupportedException();
    [MonoModIgnore] public IAvnAutomationPeerArray Children => throw new NotSupportedException();
    [MonoModIgnore] public IAvnString ClassName => throw new NotSupportedException();
    [MonoModIgnore] public IAvnAutomationPeer LabeledBy => throw new NotSupportedException();
    [MonoModIgnore] public IAvnString Name => throw new NotSupportedException();
    [MonoModIgnore] public IAvnAutomationPeer Parent => throw new NotSupportedException();
    [MonoModIgnore] public IAvnAutomationPeer TemplatedParent => throw new NotSupportedException();
    [MonoModIgnore] public IAvnAutomationPeer VisualRoot => throw new NotSupportedException();
    [MonoModIgnore] public IAvnAutomationPeer RootPeer => throw new NotSupportedException();
    [MonoModIgnore] public IAvnString HelpText => throw new NotSupportedException();
    [MonoModIgnore] public IAvnString PlaceholderText => throw new NotSupportedException();
    [MonoModIgnore] public AvnLandmarkType LandmarkType => throw new NotSupportedException();
    [MonoModIgnore] public int HeadingLevel => throw new NotSupportedException();
    [MonoModIgnore] public AvnLiveSetting LiveSetting => throw new NotSupportedException();

    [MonoModIgnore]
    public void Dispose()
    {
        throw new NotSupportedException();
    }
}

#endif