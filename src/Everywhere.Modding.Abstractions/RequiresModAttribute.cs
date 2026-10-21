namespace Everywhere.Modding.Abstractions;

/// <summary>
/// Declares a dependency required by a conditional type or method. Multiple conditions are cumulative.
/// This metadata does not insert availability checks or prevent inlining.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Method,
    AllowMultiple = true,
    Inherited = false)]
public sealed class RequiresModAttribute(string modId) : Attribute
{
    /// <summary>
    /// Gets the identifier of the dependency declared by the mod project.
    /// </summary>
    public string ModId { get; } = modId;
}