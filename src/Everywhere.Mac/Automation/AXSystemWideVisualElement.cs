using Everywhere.Automation;
using Everywhere.Mac.Interop;

namespace Everywhere.Mac.Automation;

/// <summary>
/// Represents the Context-owned AXSystemWide special root without exposing it as a traversable provider element.
/// </summary>
public sealed class AXSystemWideVisualElement(
    VisualElementIdentity identity,
    MacVisualElementBackend backend,
    AXUIElement nativeElement,
    string id
) : AXVisualElement(identity, backend, nativeElement, id)
{
    /// <inheritdoc />
    protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request)
    {
        // AXSystemWide rejects ordinary scalar batches as InvalidUIElement. Its Context identity and
        // special-root type are Backend-known local facts, so querying them must not issue provider RPCs.
        var availableFields = VisualElementFields.None;
        var id = default(string);
        var type = default(VisualElementType?);
        if (request.RequestedFields.HasFlag(VisualElementFields.Id))
        {
            id = Id;
            availableFields |= VisualElementFields.Id;
        }

        if (request.RequestedFields.HasFlag(VisualElementFields.Type))
        {
            type = VisualElementType.Unknown;
            availableFields |= VisualElementFields.Type;
        }

        return new VisualElementQueryResult(
            this,
            new VisualElementSnapshot(id, type, null, null, null, false, null, null, null),
            availableFields,
            request.RequestedFields & ~availableFields,
            null);
    }

    /// <inheritdoc />
    protected override IVisualElementEnumerator CreateEnumeratorCore(VisualElementRelation relation, VisualElementQueryRequest request)
    {
        ValidateRelation(relation);
        return EmptyVisualElementEnumerator.Shared;
    }

    /// <inheritdoc />
    protected override VisualElementTextReadResult ReadTextCore(int offset, int maxCharacters) =>
        VisualElementTextReadResult.FromFailure(new VisualElementQueryFailure(VisualElementQueryFailureKind.Unsupported, null));

    /// <inheritdoc />
    protected override void InvokeCore() => ThrowUnsupportedAction(nameof(Invoke));

    /// <inheritdoc />
    protected override void SetTextCore(string text) => ThrowUnsupportedAction(nameof(SetText));

    /// <inheritdoc />
    protected override void FocusCore() => ThrowUnsupportedAction(nameof(Focus));

    /// <inheritdoc />
    protected override string? GetSelectedTextCore(int maxCharacters) => null;

    /// <inheritdoc />
    protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken) =>
        Task.FromException<IVisualElementCapture>(new NotSupportedException("The AXSystemWide special root cannot be captured."));

    private static void ThrowUnsupportedAction(string action) =>
        throw new NotSupportedException($"The AXSystemWide special root does not support the '{action}' action.");
}