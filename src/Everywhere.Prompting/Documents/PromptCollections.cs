using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using MessagePack;

namespace Everywhere.Prompting.Documents;

/// <summary>
/// Stores validated prompt attributes in insertion order using invariant string values.
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
/// Contains ordered valueless flags emitted by a <see cref="PromptCompactElement" />.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class PromptFlagCollection : IEnumerable<string>
{
    [Key(0)]
    private List<string> Items { get; set; } = [];

    /// <summary>
    /// Gets the number of flags.
    /// </summary>
    public int Count => Items.Count;

    /// <summary>
    /// Adds a flag when it is not already present.
    /// </summary>
    /// <param name="flag">The compact markup flag name.</param>
    public void Add(string flag)
    {
        PromptXmlName.Validate(flag, nameof(flag));
        if (!Items.Contains(flag, StringComparer.Ordinal)) Items.Add(flag);
    }

    /// <summary>
    /// Removes a flag when it is present.
    /// </summary>
    /// <param name="flag">The compact markup flag name.</param>
    /// <returns><see langword="true" /> when the flag was removed.</returns>
    public bool Remove(string flag) => Items.Remove(flag);

    /// <summary>
    /// Determines whether the collection contains a flag.
    /// </summary>
    /// <param name="flag">The compact markup flag name.</param>
    /// <returns><see langword="true" /> when the flag is present.</returns>
    public bool Contains(string flag) => Items.Contains(flag, StringComparer.Ordinal);

    /// <inheritdoc />
    public IEnumerator<string> GetEnumerator() => Items.GetEnumerator();

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