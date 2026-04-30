using System.Collections;
using Avalonia;
using Everywhere.Interop;

namespace Everywhere.Core.Tests.Chat.VisualContext.Testing;

/// <summary>
/// Identifies scalar fields that the mock accessibility provider may return.
/// </summary>
[Flags]
internal enum VisualElementFields
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
/// Identifies a relation traversed by the mock accessibility provider.
/// </summary>
internal enum VisualElementRelation
{
    Parent,
    Child,
    PreviousSibling,
    NextSibling,
}

/// <summary>
/// Identifies a normalized failure injected by the mock accessibility provider.
/// </summary>
internal enum VisualElementReadFailureKind
{
    ElementUnavailable,
    Unsupported,
    Timeout,
    ProviderFailure,
}

/// <summary>
/// Describes one bounded scalar read requested from the mock accessibility provider.
/// </summary>
internal readonly record struct VisualElementReadRequest
{
    /// <summary>
    /// Gets a request for every scalar field with a bounded text preview.
    /// </summary>
    public static VisualElementReadRequest Default { get; } = new(VisualElementFields.All, 4_096);

    /// <summary>
    /// Gets the requested scalar fields.
    /// </summary>
    public VisualElementFields RequestedFields { get; }

    /// <summary>
    /// Gets the maximum number of text characters returned by the mock provider.
    /// </summary>
    public int MaxTextCharacters { get; }

    /// <summary>
    /// Initializes a bounded scalar read request.
    /// </summary>
    public VisualElementReadRequest(VisualElementFields requestedFields, int maxTextCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxTextCharacters);

        RequestedFields = requestedFields;
        MaxTextCharacters = maxTextCharacters;
    }
}

/// <summary>
/// Configures lazy enumeration of one visual relation in the mock provider.
/// </summary>
internal readonly record struct VisualElementEnumerationOptions(VisualElementReadRequest ReadRequest)
{
    /// <summary>
    /// Gets the default bounded enumeration options.
    /// </summary>
    public static VisualElementEnumerationOptions Default { get; } = new(VisualElementReadRequest.Default);
}

/// <summary>
/// Contains the requested scalar fields captured from one mock visual element.
/// </summary>
internal sealed record VisualElementSnapshot(
    string? Id,
    VisualElementType? Type,
    VisualElementStates? States,
    string? Name,
    string? TextPreview,
    PixelRect? Bounds,
    int? ProcessId,
    nint? NativeWindowHandle);

/// <summary>
/// Describes a normalized mock-provider failure without discarding its original exception.
/// </summary>
internal sealed record VisualElementReadFailure(
    VisualElementReadFailureKind Kind,
    string Message,
    Exception? Exception = null);

/// <summary>
/// Contains one bounded mock element snapshot and field-level availability information.
/// </summary>
internal sealed record VisualElementReadResult(
    IVisualElement Element,
    VisualElementSnapshot Snapshot,
    VisualElementFields AvailableFields,
    VisualElementFields MissingFields,
    VisualElementReadFailure? Failure)
{
    /// <summary>
    /// Gets whether the mock provider completed without a normalized failure.
    /// </summary>
    public bool IsSuccess => Failure is null;

    /// <summary>
    /// Gets whether any requested scalar field could not be returned.
    /// </summary>
    public bool IsPartial => MissingFields != VisualElementFields.None;
}

/// <summary>
/// Extends the standard enumerator contract with bounded-observation metadata used by the mock provider.
/// </summary>
internal interface IVisualEnumerator<out T> : IEnumerator<T>
{
    /// <summary>
    /// Gets whether <see cref="Count"/> is available without complete enumeration.
    /// </summary>
    bool HasCount { get; }

    /// <summary>
    /// Gets the logical item count when <see cref="HasCount"/> is true.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the zero-based index of the current item, or negative one when there is no current item.
    /// </summary>
    int Index { get; }

    /// <summary>
    /// Gets whether another item is available without changing <see cref="IEnumerator.Current"/>.
    /// </summary>
    bool HasMore { get; }
}

/// <summary>
/// Owns mock-provider state and every enumerator created during one bounded observation.
/// </summary>
internal interface IVisualElementReadSession : IDisposable
{
    /// <summary>
    /// Reads the requested scalar fields for one mock element.
    /// </summary>
    VisualElementReadResult ReadElement(IVisualElement element, VisualElementReadRequest request);

    /// <summary>
    /// Creates a lazy enumerator for one relation from the supplied origin.
    /// </summary>
    IVisualEnumerator<VisualElementReadResult> CreateEnumerator(
        IVisualElement origin,
        VisualElementRelation relation,
        VisualElementEnumerationOptions options);
}
