namespace Everywhere.Configuration;

/// <summary>
/// Generates an ISettingsDescriptorProvider into a top-level, non-generic, non-static partial class for the specified settings root.
/// Pass a new provider instance to the settings engine; providers are not registered globally.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GeneratedSettingsDescriptorAttribute(Type rootType) : Attribute
{
    /// <summary>
    /// Gets the model whose reachable properties receive generated descriptors.
    /// </summary>
    public Type RootType { get; } = rootType;
}