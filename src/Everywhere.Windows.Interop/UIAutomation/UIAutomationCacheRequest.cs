using Windows.Win32.UI.Accessibility;

namespace Everywhere.Windows.Interop.UIAutomation;

/// <summary>
/// Owns one operation-local UI Automation cache request.
/// </summary>
public sealed unsafe class UIAutomationCacheRequest : ComReference
{
    internal IUIAutomationCacheRequest* Pointer => GetPointer<IUIAutomationCacheRequest>();

    internal UIAutomationCacheRequest(IUIAutomationCacheRequest* pointer) : base((nint)pointer)
    {
    }
}