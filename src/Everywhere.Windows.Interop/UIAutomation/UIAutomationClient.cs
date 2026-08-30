using System.Drawing;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Everywhere.Windows.Interop.UIAutomation;

/// <summary>
/// Owns one CUIAutomation8 client endpoint and its mutable timeout policy.
/// </summary>
public sealed unsafe class UIAutomationClient : ComReference
{
    private IUIAutomation2* Pointer => GetPointer<IUIAutomation2>();

    private UIAutomationClient(IUIAutomation2* pointer) : base((nint)pointer)
    {
    }

    /// <summary>
    /// Creates a UI Automation 8 client on the calling COM-initialized thread.
    /// </summary>
    /// <returns>The newly owned client.</returns>
    public static UIAutomationClient Create()
    {
        CUIAutomation8.CreateInstance<IUIAutomation2>(out var pointer).ThrowOnFailure();
        return new UIAutomationClient(pointer);
    }

    /// <summary>
    /// Applies the connection and transaction timeout policy to this client endpoint.
    /// </summary>
    /// <param name="connectionTimeout">The provider connection timeout.</param>
    /// <param name="transactionTimeout">The provider transaction timeout.</param>
    public void ConfigureTimeouts(TimeSpan connectionTimeout, TimeSpan transactionTimeout)
    {
        Pointer->ConnectionTimeout = ToTimeoutMilliseconds(connectionTimeout);
        Pointer->TransactionTimeout = ToTimeoutMilliseconds(transactionTimeout);

        static uint ToTimeoutMilliseconds(TimeSpan timeout)
        {
            var milliseconds = Math.Ceiling(timeout.TotalMilliseconds);
            if (milliseconds is < 1 or > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    $"Windows UI Automation timeouts must be between 1 and {uint.MaxValue} milliseconds.");
            }

            return (uint)milliseconds;
        }
    }

    /// <summary>
    /// Gets a newly owned Content View TreeWalker.
    /// </summary>
    /// <returns>The owned TreeWalker reference.</returns>
    public UIAutomationTreeWalker CreateContentViewWalker()
    {
        return new UIAutomationTreeWalker(Pointer->ContentViewWalker);
    }

    /// <summary>
    /// Creates an operation-local cache request for the selected bounded fields.
    /// </summary>
    /// <param name="options">The fields and patterns to cache.</param>
    /// <returns>The owned cache request.</returns>
    public UIAutomationCacheRequest CreateCacheRequest(UIAutomationCacheOptions options)
    {
        var pCacheRequest = Pointer->CreateCacheRequest();
        try
        {
            pCacheRequest->TreeScope = TreeScope.TreeScope_Element;
            pCacheRequest->AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;

            var pCondition = Pointer->CreateTrueCondition();
            try
            {
                pCacheRequest->TreeFilter = pCondition;
            }
            finally
            {
                pCondition->Release();
            }

            AddOptions(pCacheRequest, options);
            return new UIAutomationCacheRequest(pCacheRequest);
        }
        catch
        {
            pCacheRequest->Release();
            throw;
        }
    }

    /// <summary>
    /// Acquires the focused element with the requested cache.
    /// </summary>
    /// <param name="cacheRequest">The operation-local cache request.</param>
    /// <returns>A temporary lease that is empty when no focused element is available.</returns>
    public UIAutomationElement GetFocusedElementBuildCache(UIAutomationCacheRequest cacheRequest) =>
        new(Pointer->GetFocusedElementBuildCache(cacheRequest.Pointer));

    /// <summary>
    /// Acquires the UI Automation desktop root with the requested cache.
    /// </summary>
    /// <param name="cacheRequest">The operation-local cache request.</param>
    /// <returns>A temporary lease for the desktop root.</returns>
    public UIAutomationElement GetRootElementBuildCache(UIAutomationCacheRequest cacheRequest) =>
        new(Pointer->GetRootElementBuildCache(cacheRequest.Pointer));

    /// <summary>
    /// Acquires the element at one physical screen point with the requested cache.
    /// </summary>
    /// <param name="x">The physical screen x-coordinate.</param>
    /// <param name="y">The physical screen y-coordinate.</param>
    /// <param name="cacheRequest">The operation-local cache request.</param>
    /// <returns>A temporary lease that is empty when no element is available.</returns>
    public UIAutomationElement ElementFromPointBuildCache(int x, int y, UIAutomationCacheRequest cacheRequest) =>
        new(Pointer->ElementFromPointBuildCache(new Point(x, y), cacheRequest.Pointer));

    /// <summary>
    /// Acquires the element associated with one native window with the requested cache.
    /// </summary>
    /// <param name="windowHandle">The native window handle.</param>
    /// <param name="cacheRequest">The operation-local cache request.</param>
    /// <returns>A temporary lease that is empty when no element is available.</returns>
    public UIAutomationElement ElementFromHandleBuildCache(nint windowHandle, UIAutomationCacheRequest cacheRequest) =>
        new(Pointer->ElementFromHandleBuildCache((HWND)windowHandle, cacheRequest.Pointer));

    private static void AddOptions(IUIAutomationCacheRequest* pRequest, UIAutomationCacheOptions options)
    {
        AddProperty(pRequest, options, UIAutomationCacheOptions.RuntimeId, UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId);
        AddProperty(pRequest, options, UIAutomationCacheOptions.ControlType, UIA_PROPERTY_ID.UIA_ControlTypePropertyId);
        AddProperty(pRequest, options, UIAutomationCacheOptions.BoundingRectangle, UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId);
        AddProperty(pRequest, options, UIAutomationCacheOptions.ProcessId, UIA_PROPERTY_ID.UIA_ProcessIdPropertyId);
        AddProperty(pRequest, options, UIAutomationCacheOptions.NativeWindowHandle, UIA_PROPERTY_ID.UIA_NativeWindowHandlePropertyId);
        AddProperty(pRequest, options, UIAutomationCacheOptions.IsOffscreen, UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId);
        AddProperty(pRequest, options, UIAutomationCacheOptions.IsEnabled, UIA_PROPERTY_ID.UIA_IsEnabledPropertyId);
        AddProperty(pRequest, options, UIAutomationCacheOptions.HasKeyboardFocus, UIA_PROPERTY_ID.UIA_HasKeyboardFocusPropertyId);
        AddProperty(pRequest, options, UIAutomationCacheOptions.IsSelected, UIA_PROPERTY_ID.UIA_SelectionItemIsSelectedPropertyId);
        AddProperty(pRequest, options, UIAutomationCacheOptions.IsReadOnly, UIA_PROPERTY_ID.UIA_ValueIsReadOnlyPropertyId);
        AddProperty(pRequest, options, UIAutomationCacheOptions.IsPassword, UIA_PROPERTY_ID.UIA_IsPasswordPropertyId);
        AddProperty(pRequest, options, UIAutomationCacheOptions.Name, UIA_PROPERTY_ID.UIA_NamePropertyId);
        // Caching a pattern does not implicitly cache any of its properties.
        AddProperty(pRequest, options, UIAutomationCacheOptions.Value, UIA_PROPERTY_ID.UIA_ValueValuePropertyId);
        AddProperty(pRequest, options, UIAutomationCacheOptions.ExpandCollapseState, UIA_PROPERTY_ID.UIA_ExpandCollapseExpandCollapseStatePropertyId);
        AddPattern(pRequest, options, UIAutomationCacheOptions.ValuePattern, UIA_PATTERN_ID.UIA_ValuePatternId);
        AddPattern(pRequest, options, UIAutomationCacheOptions.TextPattern, UIA_PATTERN_ID.UIA_TextPatternId);
        AddPattern(pRequest, options, UIAutomationCacheOptions.InvokePattern, UIA_PATTERN_ID.UIA_InvokePatternId);
        AddPattern(pRequest, options, UIAutomationCacheOptions.TogglePattern, UIA_PATTERN_ID.UIA_TogglePatternId);
        AddPattern(pRequest, options, UIAutomationCacheOptions.SelectionPattern, UIA_PATTERN_ID.UIA_SelectionPatternId);
        AddPattern(pRequest, options, UIAutomationCacheOptions.SelectionItemPattern, UIA_PATTERN_ID.UIA_SelectionItemPatternId);
        AddPattern(pRequest, options, UIAutomationCacheOptions.ExpandCollapsePattern, UIA_PATTERN_ID.UIA_ExpandCollapsePatternId);
        AddPattern(pRequest, options, UIAutomationCacheOptions.LegacyIAccessiblePattern, UIA_PATTERN_ID.UIA_LegacyIAccessiblePatternId);
    }

    private static void AddProperty(
        IUIAutomationCacheRequest* request,
        UIAutomationCacheOptions options,
        UIAutomationCacheOptions option,
        UIA_PROPERTY_ID propertyId)
    {
        if (options.HasFlag(option))
        {
            request->AddProperty(propertyId);
        }
    }

    private static void AddPattern(
        IUIAutomationCacheRequest* request,
        UIAutomationCacheOptions options,
        UIAutomationCacheOptions option,
        UIA_PATTERN_ID patternId)
    {
        if (options.HasFlag(option))
        {
            request->AddPattern(patternId);
        }
    }
}