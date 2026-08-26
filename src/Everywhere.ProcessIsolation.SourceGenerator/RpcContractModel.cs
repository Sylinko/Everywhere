using System.Collections.Immutable;
using System.Globalization;

namespace Everywhere.ProcessIsolation.SourceGenerator;

internal enum RpcMethodKind
{
    Response,
    Notification,
    Stream
}

internal sealed class RpcContractModel(
    string namespaceName,
    string interfaceType,
    string stem,
    string accessibility,
    ushort contractId,
    ImmutableArray<RpcMethodModel> methods,
    string hintName)
{
    public string NamespaceName { get; } = namespaceName;
    public string InterfaceType { get; } = interfaceType;
    public string Stem { get; } = stem;
    public string Accessibility { get; } = accessibility;
    public ushort ContractId { get; } = contractId;
    public ImmutableArray<RpcMethodModel> Methods { get; } = methods;
    public string HintName { get; } = hintName;
}

internal sealed class RpcMethodModel(
    string escapedName,
    string returnType,
    string requestType,
    string requestParameterName,
    string? cancellationTokenParameterName,
    string? resultType,
    ushort methodId,
    RpcMethodKind kind,
    bool returnsTask)
{
    public string EscapedName { get; } = escapedName;
    public string ReturnType { get; } = returnType;
    public string RequestType { get; } = requestType;
    public string RequestParameterName { get; } = requestParameterName;
    public string? CancellationTokenParameterName { get; } = cancellationTokenParameterName;
    public string? ResultType { get; } = resultType;
    public ushort MethodId { get; } = methodId;
    public RpcMethodKind Kind { get; } = kind;
    public bool ReturnsTask { get; } = returnsTask;
    public string OperationName => "Operation_" + MethodId.ToString("X4", CultureInfo.InvariantCulture);
}