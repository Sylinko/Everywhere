using Avalonia;
using Everywhere.Automation.I18N;
using Everywhere.I18N;

namespace Everywhere.Automation;

/// <summary>
/// Identifies the semantic type of a visual element.
/// </summary>
public enum VisualElementType
{
    Unknown,
    Label,
    TextEdit,
    Document,
    Button,
    Hyperlink,
    Image,
    CheckBox,
    RadioButton,
    ComboBox,
    ListView,
    ListViewItem,
    TreeView,
    TreeViewItem,
    DataGrid,
    DataGridItem,
    TabControl,
    TabItem,
    Table,
    TableRow,
    Menu,
    MenuItem,
    Slider,
    ScrollBar,
    ProgressBar,
    Spinner,

    ToolBar,
    StatusBar,

    Header,
    HeaderItem,

    Splitter,

    /// <summary>
    /// The most generic container element, its parent and children can be any type.
    /// </summary>
    Panel,

    /// <summary>
    /// The toplevel of a window, it's parent must be Screen or null
    /// </summary>
    TopLevel,

    /// <summary>
    /// A screen that contains toplevel, its parent is always null and children are toplevel.
    /// </summary>
    Screen
}

/// <summary>
/// Identifies observable states of a visual element.
/// </summary>
[Flags]
public enum VisualElementStates
{
    None = 0,
    Offscreen = 1 << 0,
    Disabled = 1 << 1,
    Focused = 1 << 2,
    Selected = 1 << 3,
    ReadOnly = 1 << 4,
    Password = 1 << 5,
}

/// <summary>
/// Identifies scalar fields that may be requested from a platform visual element.
/// </summary>
[Flags]
public enum VisualElementFields
{
    None = 0,
    Id = 1 << 0,
    Type = 1 << 1,
    States = 1 << 2,
    Name = 1 << 3,
    Text = 1 << 4,
    Bounds = 1 << 5,
    ProcessId = 1 << 6,
    NativeWindowHandle = 1 << 7,
    All = Id | Type | States | Name | Text | Bounds | ProcessId | NativeWindowHandle,
}

/// <summary>
/// Identifies a visual relation traversed from one element.
/// </summary>
public enum VisualElementRelation
{
    Parent,
    Child,
    PreviousSibling,
    NextSibling,
}

/// <summary>
/// Identifies a platform root acquisition mechanism that can be replayed against a <see cref="VisualContext" />.
/// </summary>
public enum VisualElementLocatorKind
{
    Default,
    Focused,
    Pointer,
    Point,
    NativeWindow,
}

/// <summary>
/// Identifies the topological element resolved from a platform locator.
/// </summary>
public enum VisualElementResolution
{
    /// <summary>
    /// Returns the element directly identified by the locator, or the platform-wide root for a default locator.
    /// </summary>
    Direct,

    /// <summary>
    /// Returns the nearest containing top-level window, or the platform-default top-level window for a default locator.
    /// </summary>
    TopLevel,

    /// <summary>
    /// Returns the containing or nearest Screen element, or the platform-default Screen for a default locator.
    /// </summary>
    Screen,
}

/// <summary>
/// Describes where a platform Backend should acquire an initial visual element before a concrete Element exists to receive the operation.
/// </summary>
/// <remarks>
/// Locators are acquisition instructions rather than stable element identities. The concrete platform Backend applies its native timeout and cache policy from the first provider call.
/// </remarks>
public readonly record struct VisualElementLocator
{
    /// <summary>
    /// Gets an unanchored locator that lets the requested resolution select its platform default.
    /// </summary>
    public static VisualElementLocator Default => default;

    /// <summary>
    /// Gets a locator for the currently focused accessibility element.
    /// </summary>
    public static VisualElementLocator Focused => new(VisualElementLocatorKind.Focused, default, 0);

    /// <summary>
    /// Gets a locator for the accessibility element under the current pointer position.
    /// </summary>
    public static VisualElementLocator Pointer => new(VisualElementLocatorKind.Pointer, default, 0);

    /// <summary>
    /// Gets the platform acquisition mechanism represented by this locator.
    /// </summary>
    public VisualElementLocatorKind Kind { get; }

    /// <summary>
    /// Gets the screen-space point used by a <see cref="VisualElementLocatorKind.Point"/> locator.
    /// </summary>
    public PixelPoint Point { get; }

    /// <summary>
    /// Gets the native handle used by a <see cref="VisualElementLocatorKind.NativeWindow"/> locator.
    /// </summary>
    public nint NativeWindowHandle { get; }

    private VisualElementLocator(VisualElementLocatorKind kind, PixelPoint point, nint nativeWindowHandle)
    {
        Kind = kind;
        Point = point;
        NativeWindowHandle = nativeWindowHandle;
    }

    /// <summary>
    /// Creates a locator for the accessibility element at a screen-space point.
    /// </summary>
    /// <param name="point">The point in physical screen pixels.</param>
    /// <returns>A point locator.</returns>
    public static VisualElementLocator FromPoint(PixelPoint point) => new(VisualElementLocatorKind.Point, point, 0);

    /// <summary>
    /// Creates a locator for the accessibility root associated with a native window.
    /// </summary>
    /// <param name="nativeWindowHandle">The nonzero native window handle.</param>
    /// <returns>A native-window locator.</returns>
    public static VisualElementLocator FromNativeWindow(nint nativeWindowHandle)
    {
        if (nativeWindowHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nativeWindowHandle), nativeWindowHandle, "A native window handle must be nonzero.");
        }

        return new VisualElementLocator(VisualElementLocatorKind.NativeWindow, default, nativeWindowHandle);
    }
}

/// <summary>
/// Identifies a normalized platform query failure.
/// </summary>
public enum VisualElementQueryFailureKind
{
    ElementUnavailable,
    Unsupported,
    Timeout,
    ProviderFailure,
}

/// <summary>
/// Describes one bounded scalar query request.
/// </summary>
public readonly record struct VisualElementQueryRequest
{
    /// <summary>
    /// Gets a request for every scalar field with a bounded text preview.
    /// </summary>
    public static VisualElementQueryRequest Default => new(VisualElementFields.All, 4_096);

    /// <summary>
    /// Gets the scalar fields requested from the provider.
    /// </summary>
    public VisualElementFields RequestedFields { get; }

    /// <summary>
    /// Gets the maximum number of text characters returned in the query preview.
    /// </summary>
    public int MaxTextCharacters { get; }

    /// <summary>
    /// Initializes a bounded scalar query request.
    /// </summary>
    /// <param name="requestedFields">The scalar fields to request from the provider.</param>
    /// <param name="maxTextCharacters">The maximum number of text characters returned in the query preview.</param>
    public VisualElementQueryRequest(VisualElementFields requestedFields, int maxTextCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxTextCharacters);

        RequestedFields = requestedFields;
        MaxTextCharacters = maxTextCharacters;
    }
}

/// <summary>
/// Configures lazy enumeration of one visual relation.
/// </summary>
/// <param name="QueryRequest">The bounded scalar query applied to every enumerated element.</param>
public readonly record struct VisualElementEnumerationOptions(VisualElementQueryRequest QueryRequest)
{
    /// <summary>
    /// Gets the default bounded enumeration options.
    /// </summary>
    public static VisualElementEnumerationOptions Default { get; } = new(VisualElementQueryRequest.Default);
}

/// <summary>
/// Contains the requested scalar fields observed for one visual element.
/// </summary>
/// <param name="Id">The stable identity within the owning Visual Context.</param>
/// <param name="Type">The semantic element type when available.</param>
/// <param name="States">The observable element states when available.</param>
/// <param name="Name">The accessibility name when available.</param>
/// <param name="TextPreview">The bounded text preview when available.</param>
/// <param name="Bounds">The screen-space bounds when available.</param>
/// <param name="ProcessId">The owning process identifier when available.</param>
/// <param name="NativeWindowHandle">The native top-level or control handle when available.</param>
public readonly record struct VisualElementSnapshot(
    string? Id,
    VisualElementType? Type,
    VisualElementStates? States,
    string? Name,
    string? TextPreview,
    PixelRect? Bounds,
    int? ProcessId,
    nint? NativeWindowHandle
);

/// <summary>
/// Describes a normalized provider failure without discarding its platform exception.
/// </summary>
public sealed record VisualElementQueryFailure
{
    /// <summary>
    /// Describes a normalized provider failure without discarding its platform exception.
    /// </summary>
    /// <param name="kind">The platform-independent failure classification.</param>
    /// <param name="message">The localized dynamic failure message.</param>
    /// <param name="exception">The original or normalized platform exception.</param>
    public VisualElementQueryFailure(VisualElementQueryFailureKind kind, IDynamicLocaleKey? message, Exception? exception = null)
    {
        Kind = kind;
        Message = message ?? GetDefaultMessage(kind);
        Exception = exception;
    }

    /// <summary>The platform-independent failure classification.</summary>
    public VisualElementQueryFailureKind Kind { get; init; }

    /// <summary>The localized dynamic failure message.</summary>
    public IDynamicLocaleKey? Message { get; init; }

    /// <summary>The original or normalized platform exception.</summary>
    public Exception? Exception { get; init; }

    private static DynamicLocaleKey GetDefaultMessage(VisualElementQueryFailureKind kind) =>
        kind switch
        {
            VisualElementQueryFailureKind.Timeout => new DynamicLocaleKey(LocaleKey.VisualContext_QueryFailure_Timeout),
            VisualElementQueryFailureKind.ElementUnavailable => new DynamicLocaleKey(LocaleKey.VisualContext_QueryFailure_ElementUnavailable),
            VisualElementQueryFailureKind.Unsupported => new DynamicLocaleKey(LocaleKey.VisualContext_QueryFailure_Unsupported),
            _ => new DynamicLocaleKey(LocaleKey.VisualContext_QueryFailure_ProviderFailure),
        };
}

/// <summary>
/// Contains one bounded element snapshot and field-level availability information.
/// </summary>
/// <param name="Element">The Context-owned platform element.</param>
/// <param name="Snapshot">The observed scalar snapshot.</param>
/// <param name="AvailableFields">The requested fields that were observed.</param>
/// <param name="MissingFields">The requested fields that could not be observed.</param>
/// <param name="Failure">The normalized provider-wide failure, if one occurred.</param>
public sealed record VisualElementQueryResult(
    VisualElement Element,
    VisualElementSnapshot Snapshot,
    VisualElementFields AvailableFields,
    VisualElementFields MissingFields,
    VisualElementQueryFailure? Failure
)
{
    /// <summary>
    /// Gets whether the provider completed without a normalized failure.
    /// </summary>
    public bool IsSuccess => Failure is null;

    /// <summary>
    /// Gets whether any requested scalar field could not be returned.
    /// </summary>
    public bool IsPartial => MissingFields != VisualElementFields.None;
}