using Windows.Win32.UI.Accessibility;

namespace Everywhere.Windows.Interop.UIAutomation;

/// <summary>
/// Owns one UI Automation TreeWalker reference.
/// </summary>
public sealed unsafe class UIAutomationTreeWalker : ComReference
{
    private IUIAutomationTreeWalker* Pointer => GetPointer<IUIAutomationTreeWalker>();

    internal UIAutomationTreeWalker(IUIAutomationTreeWalker* pointer) : base((nint)pointer)
    {
    }

    /// <summary>
    /// Navigates one relation and returns a newly owned cached element reference.
    /// </summary>
    /// <param name="origin">The origin element.</param>
    /// <param name="direction">The relation to navigate.</param>
    /// <param name="cacheRequest">The operation-local cache request.</param>
    /// <returns>An operation-scoped element whose <see cref="UIAutomationElement.HasValue" /> is <see langword="false" /> when no related element exists.</returns>
    public UIAutomationElement NavigateBuildCache(
        UIAutomationElementReference origin,
        UIAutomationNavigationDirection direction,
        UIAutomationCacheRequest cacheRequest)
    {
        var pointer = direction switch
        {
            UIAutomationNavigationDirection.Parent => Pointer->GetParentElementBuildCache(origin.Pointer, cacheRequest.Pointer),
            UIAutomationNavigationDirection.FirstChild => Pointer->GetFirstChildElementBuildCache(origin.Pointer, cacheRequest.Pointer),
            UIAutomationNavigationDirection.PreviousSibling => Pointer->GetPreviousSiblingElementBuildCache(origin.Pointer, cacheRequest.Pointer),
            UIAutomationNavigationDirection.NextSibling => Pointer->GetNextSiblingElementBuildCache(origin.Pointer, cacheRequest.Pointer),
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
        };
        return new UIAutomationElement(pointer);
    }
}