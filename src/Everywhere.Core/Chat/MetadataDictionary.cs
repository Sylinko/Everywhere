namespace Everywhere.Chat;

/// <summary>
/// Represents a dictionary for metadata storage.
/// </summary>
public sealed class MetadataDictionary : Dictionary<string, object?>
{
    public MetadataDictionary() { }

    public MetadataDictionary(int capacity) : base(capacity) { }

    public MetadataDictionary(IDictionary<string, object?> dictionary) : base(dictionary) { }
}