using Windows.Win32;
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
    private readonly IUIAutomationElement* Pointer
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
    public readonly int CachedControlType => (int)Pointer->CachedControlType;

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