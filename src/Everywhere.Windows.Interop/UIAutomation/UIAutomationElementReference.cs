using Windows.Win32.UI.Accessibility;

namespace Everywhere.Windows.Interop.UIAutomation;

/// <summary>
/// Owns one durable COM reference that keeps a UI Automation element available across operations.
/// </summary>
/// <remarks>
/// Pointer equality is not element identity. Separate instances always release their own reference even when their native pointer values or RuntimeIds are equal.
/// </remarks>
public sealed unsafe class UIAutomationElementReference : ComReference
{
    internal IUIAutomationElement* Pointer => GetPointer<IUIAutomationElement>();

    internal UIAutomationElementReference(IUIAutomationElement* pointer) : base((nint)pointer)
    {
    }

    /// <summary>
    /// Creates an operation-scoped element by adding one independent COM reference while retaining this durable reference.
    /// </summary>
    /// <returns>An independently owned operation-scoped element.</returns>
    public UIAutomationElement Acquire()
    {
        var pointer = Pointer;
        pointer->AddRef();
        return new UIAutomationElement(pointer);
    }

    /// <summary>
    /// Creates an operation-scoped element with an updated cache while retaining this durable reference.
    /// </summary>
    /// <param name="cacheRequest">The cache request created by the executing UI Automation client.</param>
    /// <returns>An independently owned operation-scoped element.</returns>
    public UIAutomationElement BuildUpdatedCache(UIAutomationCacheRequest cacheRequest)
    {
        return new UIAutomationElement(Pointer->BuildUpdatedCache(cacheRequest.Pointer));
    }
}