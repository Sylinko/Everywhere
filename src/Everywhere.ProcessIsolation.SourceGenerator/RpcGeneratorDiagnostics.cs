using Microsoft.CodeAnalysis;

namespace Everywhere.ProcessIsolation.SourceGenerator;

internal static class RpcGeneratorDiagnostics
{
    private const string Category = "Everywhere.ProcessIsolation.SourceGenerator";

    public static readonly DiagnosticDescriptor InvalidContractId = new(
        "EPIRPC001",
        "Invalid RPC contract ID",
        "RPC contract '{0}' uses contract ID zero, which is reserved for protocol operations",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateContractId = new(
        "EPIRPC002",
        "Duplicate RPC contract ID",
        "RPC contract ID {0} is also used by '{1}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidMethodAttribute = new(
        "EPIRPC003",
        "Invalid RPC method identity",
        "RPC method '{0}' must have exactly one RpcMethodAttribute",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateMethodId = new(
        "EPIRPC004",
        "Duplicate RPC method ID",
        "RPC method ID {0} is also used by '{1}' in contract '{2}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedMethod = new(
        "EPIRPC005",
        "Unsupported RPC contract member",
        "RPC member '{0}' is unsupported: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedContract = new(
        "EPIRPC006",
        "Unsupported RPC contract shape",
        "RPC contract '{0}' is unsupported: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GeneratedTypeConflict = new(
        "EPIRPC007",
        "Generated RPC type conflicts with source",
        "RPC contract '{0}' would generate '{1}', but that type already exists",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidContractName = new(
        "EPIRPC008",
        "Invalid RPC contract name",
        "RPC contract '{0}' must be a public or internal top-level interface named I{{Name}}Rpc",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}