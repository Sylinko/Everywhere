namespace Everywhere.I18N;

[AttributeUsage(AttributeTargets.All)]
public class DynamicResourceKeyAttribute(string headerKey, string? descriptionKey = null) : Attribute
{
    public string HeaderKey { get; } = headerKey;

    /// <summary>
    /// The optional description key.
    /// </summary>
    public string? DescriptionKey { get; } = descriptionKey;
}