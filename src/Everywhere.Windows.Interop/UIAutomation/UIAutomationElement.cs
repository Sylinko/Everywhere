using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Accessibility;

namespace Everywhere.Windows.Interop.UIAutomation;

/// <summary>
/// Reads an operation-scoped UI Automation RuntimeId while its native storage is valid.
/// </summary>
/// <typeparam name="TState">The caller-provided state type.</typeparam>
/// <typeparam name="TResult">The callback result type.</typeparam>
/// <param name="runtimeId">The RuntimeId components. The span is valid only for the duration of this callback.</param>
/// <param name="state">The caller-provided state.</param>
/// <returns>The callback result.</returns>
public delegate TResult UIAutomationRuntimeIdReader<in TState, out TResult>(ReadOnlySpan<int> runtimeId, TState state);

/// <summary>
/// Owns one operation-scoped UI Automation element reference and exposes its cached values.
/// </summary>
/// <remarks>
/// Treat this stack-only value as a unique owner and do not copy it. Dispose it after reading cached values. Call <see cref="Realize" /> only when a durable CLR owner is required; the returned reference owns a separate COM reference and does not invalidate this element.
/// </remarks>
public unsafe ref struct UIAutomationElement : IDisposable
{
    private const int UiaNotSupportedHResult = unchecked((int)0x80040204);

    // Provider-controlled selection collections must not turn one bounded preview into unbounded RPC traffic.
    private const int MaxSelectionPartCount = 256;

    internal readonly IUIAutomationElement* Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_pointer is null, nameof(UIAutomationElement));
            return _pointer;
        }
    }

    /// <summary>
    /// Gets whether this value currently owns a native element reference.
    /// </summary>
    public readonly bool HasValue => _pointer is not null;

    /// <summary>
    /// Gets the cached provider process identifier.
    /// </summary>
    public readonly int CachedProcessId => Pointer->CachedProcessId;

    /// <summary>
    /// Gets the cached UI Automation control type identifier.
    /// </summary>
    public readonly UIAutomationControlType CachedControlType => (UIAutomationControlType)Pointer->CachedControlType;

    /// <summary>
    /// Gets the cached native window handle.
    /// </summary>
    public readonly nint CachedNativeWindowHandle => Pointer->CachedNativeWindowHandle;

    /// <summary>
    /// Gets whether the element was cached as off-screen.
    /// </summary>
    public readonly bool CachedIsOffscreen => Pointer->CachedIsOffscreen;

    /// <summary>
    /// Gets whether the element was cached as enabled.
    /// </summary>
    public readonly bool CachedIsEnabled => Pointer->CachedIsEnabled;

    /// <summary>
    /// Gets whether the element was cached as having keyboard focus.
    /// </summary>
    public readonly bool CachedHasKeyboardFocus => Pointer->CachedHasKeyboardFocus;

    /// <summary>
    /// Gets whether the element was cached as a password field.
    /// </summary>
    public readonly bool CachedIsPassword => Pointer->CachedIsPassword;

    private IUIAutomationElement* _pointer;

    internal UIAutomationElement(IUIAutomationElement* pointer) => _pointer = pointer;

    /// <summary>
    /// Gets the cached bounding rectangle.
    /// </summary>
    public readonly UIAutomationRectangle GetCachedBoundingRectangle()
    {
        var rect = Pointer->CachedBoundingRectangle;
        return new UIAutomationRectangle(rect.left, rect.top, rect.right, rect.bottom);
    }

    /// <summary>
    /// Copies the cached accessible name into managed memory.
    /// </summary>
    public readonly string? GetCachedName()
    {
        var value = Pointer->CachedName;
        try
        {
            return value.Value is null ? null : value.ToString();
        }
        finally
        {
            PInvoke.SysFreeString(value);
        }
    }

    /// <summary>
    /// Gets the cached selection-item selected state when represented by a Boolean-compatible VARIANT.
    /// </summary>
    public readonly bool GetCachedIsSelected() => GetCachedBoolean(UIA_PROPERTY_ID.UIA_SelectionItemIsSelectedPropertyId);

    /// <summary>
    /// Gets the cached value read-only state when represented by a Boolean-compatible VARIANT.
    /// </summary>
    public readonly bool GetCachedIsReadOnly() => GetCachedBoolean(UIA_PROPERTY_ID.UIA_ValueIsReadOnlyPropertyId);

    /// <summary>
    /// Copies the cached scalar ValuePattern value into managed memory.
    /// </summary>
    /// <returns>The cached value, or <see langword="null" /> when the cached pattern is absent.</returns>
    public readonly string? GetCachedValue()
    {
        var interfaceId = IUIAutomationValuePattern.IID_Guid;
        var pPattern = (IUIAutomationValuePattern*)Pointer->GetCachedPatternAs(UIA_PATTERN_ID.UIA_ValuePatternId, in interfaceId);
        if (pPattern is null)
        {
            return null;
        }

        try
        {
            var value = pPattern->CachedValue;
            try
            {
                return value.Value is null ? null : value.ToString();
            }
            finally
            {
                PInvoke.SysFreeString(value);
            }
        }
        finally
        {
            pPattern->Release();
        }
    }

    /// <summary>
    /// Reads the cached RuntimeId without copying it into a durable managed identity.
    /// </summary>
    /// <typeparam name="TState">The state passed to the reader without a closure allocation.</typeparam>
    /// <typeparam name="TResult">The reader result type.</typeparam>
    /// <param name="state">The state passed to <paramref name="reader" />.</param>
    /// <param name="reader">The callback invoked while the native RuntimeId storage remains valid.</param>
    /// <returns>The callback result.</returns>
    /// <exception cref="InvalidOperationException">The cache does not contain a valid RuntimeId.</exception>
    public readonly TResult ReadCachedRuntimeId<TState, TResult>(TState state, UIAutomationRuntimeIdReader<TState, TResult> reader)
    {
        var value = Pointer->GetCachedPropertyValueEx(UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId, true);
        try
        {
            const VARENUM expectedType = VARENUM.VT_ARRAY | VARENUM.VT_I4;
            if (value.vt != expectedType || value.parray is null || PInvoke.SafeArrayGetDim(value.parray) != 1)
            {
                throw new InvalidOperationException("The UI Automation cache does not contain a valid RuntimeId.");
            }

            PInvoke.SafeArrayGetLBound(value.parray, 1, out var lowerBound).ThrowOnFailure();
            PInvoke.SafeArrayGetUBound(value.parray, 1, out var upperBound).ThrowOnFailure();
            if (upperBound < lowerBound)
            {
                throw new InvalidOperationException("The UI Automation cache contains an empty RuntimeId.");
            }

            void* data = null;
            PInvoke.SafeArrayAccessData(value.parray, &data).ThrowOnFailure();
            try
            {
                return reader(new ReadOnlySpan<int>(data, checked(upperBound - lowerBound + 1)), state);
            }
            finally
            {
                PInvoke.SafeArrayUnaccessData(value.parray).ThrowOnFailure();
            }
        }
        finally
        {
            PInvoke.VariantClear(&value).ThrowOnFailure();
        }
    }

    /// <summary>
    /// Reads at most the requested number of characters through the cached TextPattern.
    /// </summary>
    /// <param name="maxCharacters">The maximum number of UTF-16 characters to return.</param>
    /// <returns>The bounded text, or <see langword="null" /> when the cached pattern is absent.</returns>
    public readonly string? GetCachedText(int maxCharacters)
    {
        var interfaceId = IUIAutomationTextPattern.IID_Guid;
        var pPattern = (IUIAutomationTextPattern*)Pointer->GetCachedPatternAs(UIA_PATTERN_ID.UIA_TextPatternId, in interfaceId);
        if (pPattern is null)
        {
            return null;
        }

        try
        {
            var pRange = pPattern->DocumentRange;
            if (pRange is null)
            {
                return null;
            }

            try
            {
                var value = pRange->GetText(maxCharacters);
                try
                {
                    return value.Value is null ? null : value.ToString();
                }
                finally
                {
                    PInvoke.SysFreeString(value);
                }
            }
            finally
            {
                pRange->Release();
            }
        }
        finally
        {
            pPattern->Release();
        }
    }

    /// <summary>
    /// Reads the current selection as one bounded managed string, preferring text ranges before selected child labels.
    /// </summary>
    /// <param name="maxCharacters">The maximum number of UTF-16 characters returned across the complete selection.</param>
    /// <returns>The textual selection, or <see langword="null" /> when no nonempty selection is available.</returns>
    public readonly string? GetCachedSelectedText(int maxCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCharacters);
        if (maxCharacters == 0)
        {
            return null;
        }

        var selectedText = new StringBuilder(Math.Min(maxCharacters, 4_096));
        try
        {
            AppendSelectedTextRanges(selectedText, maxCharacters);
        }
        catch (COMException exception) when (IsUnsupported(exception))
        {
        }

        if (selectedText.Length == 0)
        {
            try
            {
                AppendSelectionPatternElements(selectedText, maxCharacters);
            }
            catch (COMException exception) when (IsUnsupported(exception))
            {
            }
        }

        if (selectedText.Length == 0)
        {
            try
            {
                AppendLegacySelectionElements(selectedText, maxCharacters);
            }
            catch (COMException exception) when (IsUnsupported(exception))
            {
            }
        }

        return selectedText.Length == 0 ? null : selectedText.ToString();
    }

    private readonly void AppendSelectedTextRanges(StringBuilder selectedText, int maxCharacters)
    {
        var interfaceId = IUIAutomationTextPattern.IID_Guid;
        var pattern = (IUIAutomationTextPattern*)Pointer->GetCachedPatternAs(UIA_PATTERN_ID.UIA_TextPatternId, in interfaceId);
        if (pattern is null)
        {
            return;
        }

        try
        {
            var ranges = pattern->GetSelection();
            if (ranges is null)
            {
                return;
            }

            try
            {
                var rangeCount = Math.Min(ranges->Length, Math.Min(maxCharacters, MaxSelectionPartCount));
                for (var index = 0; index < rangeCount && selectedText.Length < maxCharacters; index++)
                {
                    var range = ranges->GetElement(index);
                    if (range is null)
                    {
                        continue;
                    }

                    try
                    {
                        var remainingCharacters = maxCharacters - selectedText.Length;
                        var value = range->GetText(remainingCharacters);
                        try
                        {
                            var text = value.Value is null ? null : value.ToString();
                            if (string.IsNullOrEmpty(text))
                            {
                                continue;
                            }

                            selectedText.Append(text.AsSpan(0, Math.Min(text.Length, remainingCharacters)));
                        }
                        finally
                        {
                            PInvoke.SysFreeString(value);
                        }
                    }
                    finally
                    {
                        range->Release();
                    }
                }
            }
            finally
            {
                ranges->Release();
            }
        }
        finally
        {
            pattern->Release();
        }
    }

    private readonly void AppendSelectionPatternElements(StringBuilder selectedText, int maxCharacters)
    {
        var interfaceId = IUIAutomationSelectionPattern.IID_Guid;
        var pattern = (IUIAutomationSelectionPattern*)Pointer->GetCachedPatternAs(UIA_PATTERN_ID.UIA_SelectionPatternId, in interfaceId);
        if (pattern is null)
        {
            return;
        }

        try
        {
            AppendSelectedElements(selectedText, maxCharacters, pattern->GetCurrentSelection());
        }
        finally
        {
            pattern->Release();
        }
    }

    private readonly void AppendLegacySelectionElements(StringBuilder selectedText, int maxCharacters)
    {
        var interfaceId = IUIAutomationLegacyIAccessiblePattern.IID_Guid;
        var pattern = (IUIAutomationLegacyIAccessiblePattern*)Pointer->GetCachedPatternAs(
            UIA_PATTERN_ID.UIA_LegacyIAccessiblePatternId,
            in interfaceId);
        if (pattern is null)
        {
            return;
        }

        try
        {
            AppendSelectedElements(selectedText, maxCharacters, pattern->GetCurrentSelection());
        }
        finally
        {
            pattern->Release();
        }
    }

    private static void AppendSelectedElements(StringBuilder selectedText, int maxCharacters, IUIAutomationElementArray* selection)
    {
        if (selection is null)
        {
            return;
        }

        try
        {
            var selectedElementCount = Math.Min(selection->Length, Math.Min(maxCharacters, MaxSelectionPartCount));
            for (var index = 0; index < selectedElementCount && selectedText.Length < maxCharacters; index++)
            {
                var element = selection->GetElement(index);
                if (element is null)
                {
                    continue;
                }

                try
                {
                    var text = GetSelectedElementText(element);
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    if (selectedText.Length > 0)
                    {
                        if (maxCharacters - selectedText.Length <= 1)
                        {
                            break;
                        }

                        selectedText.Append('\n');
                    }

                    var remainingCharacters = maxCharacters - selectedText.Length;
                    selectedText.Append(text.AsSpan(0, Math.Min(text.Length, remainingCharacters)));
                }
                finally
                {
                    element->Release();
                }
            }
        }
        finally
        {
            selection->Release();
        }
    }

    private static string? GetSelectedElementText(IUIAutomationElement* element)
    {
        var text = default(string);
        try
        {
            text = CopyAndFree(element->CurrentName);
        }
        catch (COMException exception) when (IsUnsupported(exception))
        {
        }

        if (!string.IsNullOrEmpty(text))
        {
            return text;
        }

        var interfaceId = IUIAutomationLegacyIAccessiblePattern.IID_Guid;
        var pattern = (IUIAutomationLegacyIAccessiblePattern*)element->GetCurrentPatternAs(
            UIA_PATTERN_ID.UIA_LegacyIAccessiblePatternId,
            in interfaceId);
        if (pattern is null)
        {
            return null;
        }

        try
        {
            try
            {
                text = CopyAndFree(pattern->CurrentName);
            }
            catch (COMException exception) when (IsUnsupported(exception))
            {
            }

            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            try
            {
                return CopyAndFree(pattern->CurrentValue);
            }
            catch (COMException exception) when (IsUnsupported(exception))
            {
                return null;
            }
        }
        finally
        {
            pattern->Release();
        }
    }

    private static string? CopyAndFree(BSTR value)
    {
        try
        {
            return value.Value is null ? null : value.ToString();
        }
        finally
        {
            PInvoke.SysFreeString(value);
        }
    }

    private static bool IsUnsupported(COMException exception) => exception.HResult == UiaNotSupportedHResult;

    /// <summary>
    /// Attempts to replace the element's value through the cached ValuePattern.
    /// </summary>
    /// <param name="value">The complete replacement value.</param>
    /// <returns><see langword="true" /> when the pattern was present and accepted the value; otherwise <see langword="false" />.</returns>
    /// <remarks>The caller must first establish that the cached element is enabled and that ValuePattern is not read-only.</remarks>
    public readonly bool TrySetValue(string value)
    {
        var interfaceId = IUIAutomationValuePattern.IID_Guid;
        var pattern = (IUIAutomationValuePattern*)Pointer->GetCachedPatternAs(UIA_PATTERN_ID.UIA_ValuePatternId, in interfaceId);
        if (pattern is null)
        {
            return false;
        }

        try
        {
            fixed (char* valuePointer = value)
            {
                var nativeValue = PInvoke.SysAllocStringLen(valuePointer, checked((uint)value.Length));
                if (nativeValue.Value is null)
                {
                    throw new OutOfMemoryException("Failed to allocate the native UI Automation value.");
                }

                try
                {
                    pattern->SetValue(nativeValue);
                    return true;
                }
                finally
                {
                    PInvoke.SysFreeString(nativeValue);
                }
            }
        }
        finally
        {
            pattern->Release();
        }
    }

    /// <summary>
    /// Sets keyboard focus to this UI Automation element.
    /// </summary>
    public readonly void SetFocus() => Pointer->SetFocus();

    /// <summary>
    /// Attempts to obtain a physical screen point that can be clicked for this element.
    /// </summary>
    /// <param name="point">The physical screen point when one is available.</param>
    /// <returns><see langword="true" /> when UI Automation returned a clickable point; otherwise <see langword="false" />.</returns>
    public readonly bool TryGetClickablePoint(out UIAutomationPoint point)
    {
        var hasClickablePoint = Pointer->GetClickablePoint(out var nativePoint);
        point = hasClickablePoint ? new UIAutomationPoint(nativePoint.X, nativePoint.Y) : default;
        return hasClickablePoint;
    }

    /// <summary>
    /// Attempts to invoke the cached InvokePattern.
    /// </summary>
    /// <returns><see langword="true" /> when the pattern was present and invoked; otherwise <see langword="false" />.</returns>
    public readonly bool TryInvoke()
    {
        var interfaceId = IUIAutomationInvokePattern.IID_Guid;
        var pattern = (IUIAutomationInvokePattern*)Pointer->GetCachedPatternAs(UIA_PATTERN_ID.UIA_InvokePatternId, in interfaceId);
        if (pattern is null)
        {
            return false;
        }

        try
        {
            pattern->Invoke();
            return true;
        }
        finally
        {
            pattern->Release();
        }
    }

    /// <summary>
    /// Attempts to advance the cached TogglePattern state.
    /// </summary>
    /// <returns><see langword="true" /> when the pattern was present and toggled; otherwise <see langword="false" />.</returns>
    public readonly bool TryToggle()
    {
        var interfaceId = IUIAutomationTogglePattern.IID_Guid;
        var pattern = (IUIAutomationTogglePattern*)Pointer->GetCachedPatternAs(UIA_PATTERN_ID.UIA_TogglePatternId, in interfaceId);
        if (pattern is null)
        {
            return false;
        }

        try
        {
            pattern->Toggle();
            return true;
        }
        finally
        {
            pattern->Release();
        }
    }

    /// <summary>
    /// Attempts to select the element through the cached SelectionItemPattern.
    /// </summary>
    /// <returns><see langword="true" /> when the pattern was present and selected; otherwise <see langword="false" />.</returns>
    public readonly bool TrySelect()
    {
        var interfaceId = IUIAutomationSelectionItemPattern.IID_Guid;
        var pattern = (IUIAutomationSelectionItemPattern*)Pointer->GetCachedPatternAs(UIA_PATTERN_ID.UIA_SelectionItemPatternId, in interfaceId);
        if (pattern is null)
        {
            return false;
        }

        try
        {
            pattern->Select();
            return true;
        }
        finally
        {
            pattern->Release();
        }
    }

    /// <summary>
    /// Attempts to change the element's expansion through the cached ExpandCollapsePattern.
    /// </summary>
    /// <returns><see langword="true" /> when the element was expanded or collapsed; otherwise <see langword="false" /> when the pattern was absent or represented a leaf node.</returns>
    public readonly bool TryToggleExpansion()
    {
        var interfaceId = IUIAutomationExpandCollapsePattern.IID_Guid;
        var pattern = (IUIAutomationExpandCollapsePattern*)Pointer->GetCachedPatternAs(UIA_PATTERN_ID.UIA_ExpandCollapsePatternId, in interfaceId);
        if (pattern is null)
        {
            return false;
        }

        try
        {
            switch (pattern->CachedExpandCollapseState)
            {
                case ExpandCollapseState.ExpandCollapseState_Collapsed:
                case ExpandCollapseState.ExpandCollapseState_PartiallyExpanded:
                    pattern->Expand();
                    return true;
                case ExpandCollapseState.ExpandCollapseState_Expanded:
                    pattern->Collapse();
                    return true;
                case ExpandCollapseState.ExpandCollapseState_LeafNode:
                    return false;
                default:
                    throw new InvalidOperationException("The UI Automation provider returned an unknown ExpandCollapse state.");
            }
        }
        finally
        {
            pattern->Release();
        }
    }

    /// <summary>
    /// Attempts to perform the cached LegacyIAccessible default action.
    /// </summary>
    /// <returns><see langword="true" /> when the compatibility pattern was present and accepted the action; otherwise <see langword="false" />.</returns>
    public readonly bool TryDoLegacyDefaultAction()
    {
        var interfaceId = IUIAutomationLegacyIAccessiblePattern.IID_Guid;
        var pattern = (IUIAutomationLegacyIAccessiblePattern*)Pointer->GetCachedPatternAs(
            UIA_PATTERN_ID.UIA_LegacyIAccessiblePatternId,
            in interfaceId);
        if (pattern is null)
        {
            return false;
        }

        try
        {
            pattern->DoDefaultAction();
            return true;
        }
        finally
        {
            pattern->Release();
        }
    }

    /// <summary>
    /// Creates a durable CLR owner by adding one independent COM reference to this element.
    /// </summary>
    /// <returns>A durable owner that must be disposed independently.</returns>
    /// <remarks>
    /// This element remains valid and continues to own its original reference. Repeated calls are valid and each returned owner corresponds to a separate <c>AddRef</c> that must later be balanced by disposal.
    /// </remarks>
    public readonly UIAutomationElementReference Realize()
    {
        var pointer = Pointer;
        pointer->AddRef();
        try
        {
            return new UIAutomationElementReference(pointer);
        }
        catch
        {
            pointer->Release();
            throw;
        }
    }

    /// <summary>
    /// Releases the operation-scoped native reference.
    /// </summary>
    public void Dispose()
    {
        var pointer = _pointer;
        _pointer = null;
        if (pointer is not null)
        {
            pointer->Release();
        }
    }

    private readonly bool GetCachedBoolean(UIA_PROPERTY_ID propertyId)
    {
        var value = Pointer->GetCachedPropertyValueEx(propertyId, true);
        try
        {
            return value.vt switch
            {
                VARENUM.VT_BOOL => value.boolVal.Value != 0,
                VARENUM.VT_I4 or VARENUM.VT_INT => value.lVal != 0,
                VARENUM.VT_UI4 or VARENUM.VT_UINT => value.ulVal != 0,
                _ => false,
            };
        }
        finally
        {
            PInvoke.VariantClear(&value).ThrowOnFailure();
        }
    }
}