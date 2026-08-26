using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Everywhere.ProcessIsolation.SourceGenerator;

/// <summary>Generates static RPC clients and binders from annotated contract interfaces.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class ProcessIsolationRpcGenerator : IIncrementalGenerator
{
    private const string ContractAttributeName = "Everywhere.ProcessIsolation.Rpc.RpcContractAttribute";
    private const string MethodAttributeName = "Everywhere.ProcessIsolation.Rpc.RpcMethodAttribute";

    private static readonly SymbolDisplayFormat TypeDisplayFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var contracts = context.SyntaxProvider.ForAttributeWithMetadataName(
                ContractAttributeName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, _) => (INamedTypeSymbol)attributeContext.TargetSymbol)
            .Collect();

        context.RegisterSourceOutput(
            contracts.Combine(context.CompilationProvider),
            static (productionContext, input) => Execute(productionContext, input.Left, input.Right));
    }

    private static void Execute(SourceProductionContext context, ImmutableArray<INamedTypeSymbol> contractSymbols, Compilation compilation)
    {
        var symbols = DistinctSymbols(contractSymbols);
        var contractIds = new Dictionary<ushort, INamedTypeSymbol>();
        var duplicateContracts = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var symbol in symbols)
        {
            if (!TryGetId(symbol, ContractAttributeName, out var contractId, out var attributeLocation))
            {
                continue;
            }

            if (contractId == 0)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        RpcGeneratorDiagnostics.InvalidContractId,
                        attributeLocation ?? symbol.Locations.FirstOrDefault(),
                        symbol.Name));
                duplicateContracts.Add(symbol);
                continue;
            }

            if (contractIds.TryGetValue(contractId, out var existing))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        RpcGeneratorDiagnostics.DuplicateContractId,
                        attributeLocation ?? symbol.Locations.FirstOrDefault(),
                        contractId,
                        existing.ToDisplayString()));
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        RpcGeneratorDiagnostics.DuplicateContractId,
                        existing.Locations.FirstOrDefault(),
                        contractId,
                        symbol.ToDisplayString()));
                duplicateContracts.Add(existing);
                duplicateContracts.Add(symbol);
                continue;
            }

            contractIds.Add(contractId, symbol);
        }

        foreach (var symbol in symbols)
        {
            if (duplicateContracts.Contains(symbol) ||
                !TryGetId(symbol, ContractAttributeName, out var contractId, out _))
            {
                continue;
            }

            var model = BuildContract(context, compilation, symbol, contractId);
            if (model is null)
            {
                continue;
            }

            context.AddSource(model.HintName, RpcSourceEmitter.Generate(model));
        }
    }

    private static ImmutableArray<INamedTypeSymbol> DistinctSymbols(ImmutableArray<INamedTypeSymbol> symbols)
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var result = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        foreach (var symbol in symbols)
        {
            if (seen.Add(symbol))
            {
                result.Add(symbol);
            }
        }

        return result.ToImmutable();
    }

    private static RpcContractModel? BuildContract(
        SourceProductionContext context,
        Compilation compilation,
        INamedTypeSymbol symbol,
        ushort contractId)
    {
        var valid = true;
        if (symbol.TypeKind != TypeKind.Interface ||
            symbol.ContainingType is not null ||
            symbol.TypeParameters.Length != 0 ||
            symbol.Interfaces.Length != 0)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    RpcGeneratorDiagnostics.UnsupportedContract,
                    symbol.Locations.FirstOrDefault(),
                    symbol.Name,
                    "contracts must be non-generic top-level interfaces without inheritance"));
            valid = false;
        }

        if (!TryGetContractStem(symbol, out var stem, out var accessibility))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    RpcGeneratorDiagnostics.InvalidContractName,
                    symbol.Locations.FirstOrDefault(),
                    symbol.Name));
            return null;
        }

        var generatedNames = new[]
        {
            stem + "RpcOperations",
            stem + "RpcClient",
            stem + "RpcBinding"
        };
        foreach (var generatedName in generatedNames)
        {
            var metadataName = symbol.ContainingNamespace.IsGlobalNamespace ?
                generatedName :
                symbol.ContainingNamespace.ToDisplayString() + "." + generatedName;
            if (compilation.GetTypeByMetadataName(metadataName) is null)
            {
                continue;
            }

            context.ReportDiagnostic(
                Diagnostic.Create(
                    RpcGeneratorDiagnostics.GeneratedTypeConflict,
                    symbol.Locations.FirstOrDefault(),
                    symbol.Name,
                    metadataName));
            valid = false;
        }

        foreach (var member in symbol.GetMembers())
        {
            if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary })
            {
                continue;
            }

            if (member.IsImplicitlyDeclared)
            {
                continue;
            }

            context.ReportDiagnostic(
                Diagnostic.Create(
                    RpcGeneratorDiagnostics.UnsupportedMethod,
                    member.Locations.FirstOrDefault(),
                    member.Name,
                    "contracts may contain ordinary methods only"));
            valid = false;
        }

        var methods = new List<RpcMethodModel>();
        var methodIds = new Dictionary<ushort, IMethodSymbol>();
        foreach (var method in symbol.GetMembers().OfType<IMethodSymbol>()
                     .Where(static method => method.MethodKind == MethodKind.Ordinary)
                     .OrderBy(static method => method.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue))
        {
            var attributes = method.GetAttributes()
                .Where(static attribute => attribute.AttributeClass?.ToDisplayString() == MethodAttributeName)
                .ToArray();
            if (attributes.Length != 1 ||
                attributes[0].ConstructorArguments.Length != 1 ||
                attributes[0].ConstructorArguments[0].Value is null)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        RpcGeneratorDiagnostics.InvalidMethodAttribute,
                        method.Locations.FirstOrDefault(),
                        method.Name));
                valid = false;
                continue;
            }

            var methodId = Convert.ToUInt16(attributes[0].ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
            if (methodIds.TryGetValue(methodId, out var existing))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        RpcGeneratorDiagnostics.DuplicateMethodId,
                        attributes[0].ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? method.Locations.FirstOrDefault(),
                        methodId,
                        existing.Name,
                        symbol.Name));
                valid = false;
                continue;
            }

            methodIds.Add(methodId, method);
            var methodModel = BuildMethod(context, compilation, method, methodId);
            if (methodModel is null)
            {
                valid = false;
                continue;
            }

            methods.Add(methodModel);
        }

        if (!valid)
        {
            return null;
        }

        var namespaceName = symbol.ContainingNamespace.IsGlobalNamespace ? string.Empty : symbol.ContainingNamespace.ToDisplayString();
        var interfaceType = symbol.ToDisplayString(TypeDisplayFormat);
        var hintPrefix = string.IsNullOrEmpty(namespaceName) ? stem : namespaceName + "." + stem;
        return new RpcContractModel(
            namespaceName,
            interfaceType,
            stem,
            accessibility,
            contractId,
            methods.ToImmutableArray(),
            hintPrefix.Replace('<', '_').Replace('>', '_') + ".Rpc.g.cs");
    }

    private static RpcMethodModel? BuildMethod(SourceProductionContext context, Compilation compilation, IMethodSymbol method, ushort methodId)
    {
        string? unsupportedReason = null;
        if (method.IsStatic || !method.IsAbstract || method.IsGenericMethod || method.IsVararg)
        {
            unsupportedReason = "methods must be non-static, abstract, non-generic, and non-variadic";
        }
        else if (method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            unsupportedReason = "by-reference returns are not supported";
        }
        else if (method.Parameters.Length is < 1 or > 2)
        {
            unsupportedReason = "methods require one payload parameter and an optional trailing CancellationToken";
        }

        var cancellationTokenType = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
        var hasCancellationToken = false;
        if (unsupportedReason is null && method.Parameters.Length == 2)
        {
            var token = method.Parameters[1];
            if (cancellationTokenType is null ||
                !SymbolEqualityComparer.Default.Equals(token.Type, cancellationTokenType) ||
                token.RefKind != RefKind.None ||
                !token.HasExplicitDefaultValue)
            {
                unsupportedReason = "the second parameter must be an optional CancellationToken";
            }
            else
            {
                hasCancellationToken = true;
            }
        }

        if (unsupportedReason is null)
        {
            var request = method.Parameters[0];
            if (request.RefKind != RefKind.None ||
                request.IsParams ||
                request.Type.SpecialType == SpecialType.System_Object ||
                request.Type.TypeKind is TypeKind.Delegate or TypeKind.Error ||
                (cancellationTokenType is not null && SymbolEqualityComparer.Default.Equals(request.Type, cancellationTokenType)))
            {
                unsupportedReason = "the payload must be one concrete, by-value DTO parameter";
            }
        }

        var kind = RpcMethodKind.Response;
        ITypeSymbol? resultType = null;
        if (unsupportedReason is null && !TryClassifyReturnType(compilation, method.ReturnType, out kind, out resultType))
        {
            unsupportedReason = "return type must be Task, ValueTask, Task<T>, ValueTask<T>, or IAsyncEnumerable<T>";
        }

        if (unsupportedReason is not null)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    method.IsGenericMethod ? RpcGeneratorDiagnostics.UnsupportedContract : RpcGeneratorDiagnostics.UnsupportedMethod,
                    method.Locations.FirstOrDefault(),
                    method.Name,
                    unsupportedReason));
            return null;
        }

        var requestParameter = method.Parameters[0];
        return new RpcMethodModel(
            EscapeIdentifier(method.Name),
            method.ReturnType.ToDisplayString(TypeDisplayFormat),
            requestParameter.Type.ToDisplayString(TypeDisplayFormat),
            EscapeIdentifier(requestParameter.Name),
            hasCancellationToken ? EscapeIdentifier(method.Parameters[1].Name) : null,
            resultType?.ToDisplayString(TypeDisplayFormat),
            methodId,
            kind,
            IsTask(method.ReturnType));
    }

    private static bool TryClassifyReturnType(Compilation compilation, ITypeSymbol returnType, out RpcMethodKind kind, out ITypeSymbol? resultType)
    {
        resultType = null;
        var taskType = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        var valueTaskType = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
        if (SymbolEqualityComparer.Default.Equals(returnType, taskType) || SymbolEqualityComparer.Default.Equals(returnType, valueTaskType))
        {
            kind = RpcMethodKind.Notification;
            return true;
        }

        if (returnType is INamedTypeSymbol { IsGenericType: true } namedReturn)
        {
            var taskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
            var valueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
            if (SymbolEqualityComparer.Default.Equals(namedReturn.OriginalDefinition, taskOfT) ||
                SymbolEqualityComparer.Default.Equals(namedReturn.OriginalDefinition, valueTaskOfT))
            {
                kind = RpcMethodKind.Response;
                resultType = namedReturn.TypeArguments[0];
                return true;
            }

            var asyncEnumerable = compilation.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerable`1");
            if (SymbolEqualityComparer.Default.Equals(namedReturn.OriginalDefinition, asyncEnumerable))
            {
                kind = RpcMethodKind.Stream;
                resultType = namedReturn.TypeArguments[0];
                return true;
            }
        }

        kind = RpcMethodKind.Response;
        return false;
    }

    private static bool IsTask(ITypeSymbol returnType)
    {
        if (returnType is INamedTypeSymbol namedType)
        {
            return namedType.Name == "Task" && namedType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks";
        }

        return false;
    }

    private static bool TryGetId(ISymbol symbol, string attributeName, out ushort id, out Location? location)
    {
        var attribute = symbol.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == attributeName);
        location = attribute?.ApplicationSyntaxReference?.GetSyntax().GetLocation();
        if (attribute is null ||
            attribute.ConstructorArguments.Length != 1 ||
            attribute.ConstructorArguments[0].Value is null)
        {
            id = 0;
            return false;
        }

        id = Convert.ToUInt16(attribute.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryGetContractStem(INamedTypeSymbol symbol, out string stem, out string accessibility)
    {
        var name = symbol.Name;
        if (name.Length <= 4 ||
            name[0] != 'I' ||
            !name.EndsWith("Rpc", StringComparison.Ordinal) ||
            symbol.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
        {
            stem = string.Empty;
            accessibility = string.Empty;
            return false;
        }

        stem = name.Substring(1, name.Length - 4);
        accessibility = symbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        return SyntaxFacts.IsValidIdentifier(stem);
    }

    private static string EscapeIdentifier(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ||
        SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None ?
            "@" + identifier :
            identifier;
}