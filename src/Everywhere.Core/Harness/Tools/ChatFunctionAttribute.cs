namespace Everywhere.Harness;

/// <summary>
/// Specifies the model-exposed name for a tool method registered with Everywhere.Harness.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class ChatFunctionAttribute(string name) : Attribute
{
    /// <summary>
    /// Gets the model-exposed function name.
    /// </summary>
    public string Name { get; } = name;
}

/// <summary>
/// Marks a tool parameter to be resolved from the active invocation <see cref="IServiceProvider"/>
/// and excluded from the model-visible JSON schema.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromChatServicesAttribute : Attribute;
