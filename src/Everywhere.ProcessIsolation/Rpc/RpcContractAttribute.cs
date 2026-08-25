namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>
/// Assigns the stable contract number used to compose a 32-bit operation ID.
/// The number is a wire ABI and must not be changed after a released peer exists.
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class RpcContractAttribute(ushort contractId) : Attribute
{
    /// <summary>Gets the stable contract number.</summary>
    public ushort ContractId { get; } = contractId;
}

/// <summary>Assigns a request/response method number within an RPC contract.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class RpcMethodAttribute(ushort methodId) : Attribute
{
    /// <summary>Gets the stable method number.</summary>
    public ushort MethodId { get; } = methodId;
}

/// <summary>Assigns a one-way notification number within an RPC contract.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class RpcNotificationAttribute(ushort methodId) : Attribute
{
    /// <summary>Gets the stable notification number.</summary>
    public ushort MethodId { get; } = methodId;
}