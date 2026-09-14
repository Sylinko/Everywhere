using Everywhere.ProcessIsolation.Roles;

namespace Everywhere.ProcessIsolation;

/// <summary>
/// Documents that a type belongs to a specific Host role in the production process-isolation layout.
/// </summary>
/// <remarks>
/// This marker has no runtime behavior. It does not control dependency injection, RPC generation, scheduling, or local test usage.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
#pragma warning disable CS9113
public sealed class InHostProcessAttribute(ProcessRole role) : Attribute;
#pragma warning restore CS9113