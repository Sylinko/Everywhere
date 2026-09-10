namespace Everywhere.Mac.Interop;

/// <summary>
/// Owns the positional results of one AX scalar-attribute transaction.
/// </summary>
/// <remarks>
/// Accessibility returns one owned array whose items are borrowed. Each getter copies a borrowed item directly into
/// a managed scalar without creating an NSObject wrapper. This object is intentionally operation-local; it is not a
/// cache across queries because AX properties are mutable provider state.
/// </remarks>
public sealed class AXAttributeBatch : IDisposable
{
    public AXError Error { get; }

    private readonly IReadOnlyDictionary<nint, int> _indices;
    private NSArray? _values;

    private AXAttributeBatch(AXError error, NSArray? values, IReadOnlyDictionary<nint, int> indices)
    {
        Error = error;
        _values = values;
        _indices = indices;
    }

    public static AXAttributeBatch Copy(AXUIElement element, IReadOnlyList<NSString> attributes)
    {
        var indices = new Dictionary<nint, int>(attributes.Count);
        var objects = new NSObject[attributes.Count];
        for (var index = 0; index < attributes.Count; index++)
        {
            var attribute = attributes[index];
            objects[index] = attribute;
            indices.Add(attribute.Handle.Handle, index);
        }

        using var nativeAttributes = NSArray.FromNSObjects(objects);
        var error = element.CopyMultipleAttributeValues(nativeAttributes, out var values);
        return new AXAttributeBatch(error, values, indices);
    }

    public AXError GetString(NSString attribute, out string? value)
    {
        var error = GetSlot(attribute, out var slot);
        value = error == AXError.Success && AXScalarValueReader.TryReadString(slot, out var result) ? result : null;
        return error == AXError.Success && value is null ? AXError.Failure : error;
    }

    public AXError GetBoolean(NSString attribute, out bool? value)
    {
        var error = GetSlot(attribute, out var slot);
        value = error == AXError.Success && AXScalarValueReader.TryReadBoolean(slot, out var result) ? result : null;
        return error == AXError.Success && value is null ? AXError.Failure : error;
    }

    public AXError GetInt64(NSString attribute, out long? value)
    {
        var error = GetSlot(attribute, out var slot);
        value = error == AXError.Success && AXScalarValueReader.TryReadInt64(slot, out var result) ? result : null;
        return error == AXError.Success && value is null ? AXError.Failure : error;
    }

    public AXError GetPoint(NSString attribute, out CGPoint? value)
    {
        var error = GetSlot(attribute, out var slot);
        value = error == AXError.Success && AXScalarValueReader.TryReadPoint(slot, out var result) ? result : null;
        return error == AXError.Success && value is null ? AXError.Failure : error;
    }

    public AXError GetSize(NSString attribute, out CGSize? value)
    {
        var error = GetSlot(attribute, out var slot);
        value = error == AXError.Success && AXScalarValueReader.TryReadSize(slot, out var result) ? result : null;
        return error == AXError.Success && value is null ? AXError.Failure : error;
    }

    public void Dispose()
    {
        _values?.Dispose();
        _values = null;
    }

    private AXError GetSlot(NSString attribute, out nint value)
    {
        value = 0;
        if (Error != AXError.Success)
        {
            return Error;
        }

        if (_values is not { } values || !_indices.TryGetValue(attribute.Handle.Handle, out var index) || (nuint)index >= values.Count)
        {
            return AXError.Failure;
        }

        value = values.ValueAt((nuint)index).Handle;
        return AXScalarValueReader.GetSlotError(value);
    }
}