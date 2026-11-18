using System.Collections.Immutable;
using System.Globalization;

namespace Everywhere.ProcessIsolation.SourceGenerator;

/// <summary>
/// Identifies the transport operation shape.
/// </summary>
internal enum RpcMethodKind
{
    Response,
    Notification,
    Stream
}

/// <summary>
/// Contains symbol-free values for one generated contract file.
/// </summary>
internal sealed class RpcContractModel(
    string namespaceName,
    string interfaceType,
    string stem,
    string accessibility,
    ushort contractId,
    ImmutableArray<RpcMethodModel> methods,
    string hintName
) : IEquatable<RpcContractModel>
{
    /// <summary>
    /// Gets the namespace for the generated contract types.
    /// </summary>
    public string NamespaceName { get; } = namespaceName;

    /// <summary>
    /// Gets the fully qualified interface name implemented by the client.
    /// </summary>
    public string InterfaceType { get; } = interfaceType;

    /// <summary>
    /// Gets the shared name prefix for operations, client, and binding types.
    /// </summary>
    public string Stem { get; } = stem;

    /// <summary>
    /// Gets the accessibility keyword inherited from the contract.
    /// </summary>
    public string Accessibility { get; } = accessibility;

    /// <summary>
    /// Gets the wire contract ID.
    /// </summary>
    public ushort ContractId { get; } = contractId;

    /// <summary>
    /// Gets methods in declaration order.
    /// </summary>
    public ImmutableArray<RpcMethodModel> Methods { get; } = methods;

    /// <summary>
    /// Gets the stable generated file name.
    /// </summary>
    public string HintName { get; } = hintName;

    /// <inheritdoc />
    public bool Equals(RpcContractModel? other)
    {
        return other is not null && NamespaceName == other.NamespaceName && InterfaceType == other.InterfaceType &&
            Stem == other.Stem && Accessibility == other.Accessibility && ContractId == other.ContractId &&
            HintName == other.HintName && Methods.SequenceEqual(other.Methods);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RpcContractModel other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HintName.GetHashCode();
}

/// <summary>
/// Contains the resolved signature and transport shape of one method.
/// </summary>
internal sealed class RpcMethodModel(
    string escapedName,
    string returnType,
    string requestType,
    string requestParameterName,
    string? cancellationTokenParameterName,
    string? resultType,
    ushort methodId,
    RpcMethodKind kind,
    bool returnsTask
) : IEquatable<RpcMethodModel>
{
    /// <summary>
    /// Gets the method name escaped for use as a C# identifier.
    /// </summary>
    public string EscapedName { get; } = escapedName;

    /// <summary>
    /// Gets the fully qualified declared return type.
    /// </summary>
    public string ReturnType { get; } = returnType;

    /// <summary>
    /// Gets the fully qualified payload type.
    /// </summary>
    public string RequestType { get; } = requestType;

    /// <summary>
    /// Gets the escaped payload parameter name.
    /// </summary>
    public string RequestParameterName { get; } = requestParameterName;

    /// <summary>
    /// Gets the optional cancellation parameter name.
    /// </summary>
    public string? CancellationTokenParameterName { get; } = cancellationTokenParameterName;

    /// <summary>
    /// Gets the response or stream element type, when present.
    /// </summary>
    public string? ResultType { get; } = resultType;

    /// <summary>
    /// Gets the wire method ID within the contract.
    /// </summary>
    public ushort MethodId { get; } = methodId;

    /// <summary>
    /// Gets the response, notification, or stream shape.
    /// </summary>
    public RpcMethodKind Kind { get; } = kind;

    /// <summary>
    /// Gets whether the declared return uses Task rather than ValueTask.
    /// </summary>
    public bool ReturnsTask { get; } = returnsTask;

    /// <summary>
    /// Gets the generated constant name for this wire operation.
    /// </summary>
    public string OperationName => "Operation_" + MethodId.ToString("X4", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public bool Equals(RpcMethodModel? other)
    {
        return other is not null && EscapedName == other.EscapedName && ReturnType == other.ReturnType &&
            RequestType == other.RequestType && RequestParameterName == other.RequestParameterName &&
            CancellationTokenParameterName == other.CancellationTokenParameterName && ResultType == other.ResultType &&
            MethodId == other.MethodId && Kind == other.Kind && ReturnsTask == other.ReturnsTask;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RpcMethodModel other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => MethodId.GetHashCode();
}