using System.Globalization;
using System.Xml;
using MessagePack;

namespace Everywhere.Chat.Documents;

/// <summary>
/// Stores validated XML attributes in insertion order using invariant string values.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class PromptAttributeCollection : IEnumerable<KeyValuePair<string, string>>
{
    [Key(0)]
    private Dictionary<string, string> Items { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets an attribute, converting assigned values with invariant culture.
    /// </summary>
    public object? this[string name]
    {
        get => Items.GetValueOrDefault(name);
        set
        {
            PromptXmlName.Validate(name, nameof(name));
            Items[name] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    /// <summary>
    /// Gets the number of attributes.
    /// </summary>
    public int Count => Items.Count;

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Items.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Validates the restricted XML names accepted by prompt elements and attributes.
/// </summary>
internal static class PromptXmlName
{
    public static void Validate(string name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);

        if (TryVerifyName(null, name) is { } exception)
        {
            throw new ArgumentException($"'{name}' is not a valid XML name.", parameterName, exception);
        }
    }

    /// <summary>
    /// internal static Exception TryVerifyName(string name)
    /// </summary>
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod)]
    private extern static Exception? TryVerifyName(XmlConvert? klass, string name);
}