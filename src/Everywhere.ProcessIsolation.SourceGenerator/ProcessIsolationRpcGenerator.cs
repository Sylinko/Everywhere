using System.Collections.Immutable;
using System.Globalization;
using Everywhere.SourceGeneration;
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

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var contracts = context.SyntaxProvider.ForAttributeWithMetadataName(
            ContractAttributeName,
            static (node, _) => node is InterfaceDeclarationSyntax,
            static (attributeContext, token) => AnalyzeContract(attributeContext, token));

        // Only ID uniqueness is global. Method models and source formatting remain per contract.
        var conflicts = contracts.Select(static (contract, _) => contract.Identity).Collect()
            .Select(static (identities, token) => ValidateIdentities(identities, token));
        var checkedContracts = contracts.Combine(conflicts).Select(static (input, _) =>
            input.Right.TryGetValue(input.Left.Identity.Name, out var diagnostics) ?
                new AnalysisResult<RpcContractModel>(null, diagnostics) :
                input.Left.Result);
        context.RegisterSourceOutput(
            checkedContracts,
            static (production, result) =>
            {
                foreach (var diagnostic in result.Diagnostics)
                {
                    production.ReportDiagnostic(diagnostic);
                }
            });

        var models = checkedContracts.Select(static (result, _) => result.Model).WithTrackingName("RpcModel");
        var sources = models.Select(static (model, token) =>
        {
            token.ThrowIfCancellationRequested();
            return model is null ? ((string HintName, string Source)?)null : (model.HintName, RpcSourceEmitter.Generate(model, token));
        }).WithTrackingName("RpcSource");
        context.RegisterSourceOutput(
            sources,
            static (production, source) =>
            {
                if (source is { } value)
                {
                    production.AddSource(value.HintName, value.Source);
                }
            });
    }

    /// <summary>
    /// Resolves method signatures inside the semantic transform, before any models are cached.
    /// </summary>
    private static ContractAnalysis AnalyzeContract(GeneratorAttributeSyntaxContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var symbol = (INamedTypeSymbol)context.TargetSymbol;
        var analysis = new AnalysisContext(token);
        var hasId = TryGetId(symbol, ContractAttributeName, token, out var id, out var location);
        var identity = new ContractIdentity(
            symbol.ToDisplayString(),
            symbol.Name,
            hasId ? id : null,
            location ?? symbol.Locations.FirstOrDefault(),
            symbol.Locations.FirstOrDefault());
        var model = hasId && id != 0 ? BuildContract(analysis, context.SemanticModel.Compilation, symbol, id) : null;
        return new ContractAnalysis(identity, analysis.Complete(model));
    }

    /// <summary>
    /// Preserves duplicate-ID diagnostics and suppresses every conflicting contract's source.
    /// </summary>
    private static ImmutableDictionary<string, ImmutableArray<Diagnostic>> ValidateIdentities(
        ImmutableArray<ContractIdentity> identities,
        CancellationToken token)
    {
        var firstById = new Dictionary<ushort, ContractIdentity>();
        var diagnostics = new Dictionary<string, List<Diagnostic>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(identity.Name) || identity.Id is not { } id)
            {
                continue;
            }

            if (id == 0)
            {
                Add(identity.Name, Diagnostic.Create(RpcGeneratorDiagnostics.InvalidContractId, identity.AttributeLocation, identity.ShortName));
            }
            else if (firstById.TryGetValue(id, out var first))
            {
                Add(identity.Name, Diagnostic.Create(RpcGeneratorDiagnostics.DuplicateContractId, identity.AttributeLocation, id, first.Name));
                Add(first.Name, Diagnostic.Create(RpcGeneratorDiagnostics.DuplicateContractId, first.TypeLocation, id, identity.Name));
            }
            else
            {
                firstById.Add(id, identity);
            }
        }
        return diagnostics.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray(), StringComparer.Ordinal);

        void Add(string name, Diagnostic diagnostic)
        {
            if (!diagnostics.TryGetValue(name, out var entries))
            {
                entries = [];
                diagnostics.Add(name, entries);
            }
            entries.Add(diagnostic);
        }
    }

    private static RpcContractModel? BuildContract(
        AnalysisContext context,
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
            context.CancellationToken.ThrowIfCancellationRequested();
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
            context.CancellationToken.ThrowIfCancellationRequested();
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
                        attributes[0].ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ??
                        method.Locations.FirstOrDefault(),
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

    private static RpcMethodModel? BuildMethod(AnalysisContext context, Compilation compilation, IMethodSymbol method, ushort methodId)
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

    private static bool TryGetId(ISymbol symbol, string attributeName, CancellationToken token, out ushort id, out Location? location)
    {
        var attribute = symbol.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == attributeName);
        location = attribute?.ApplicationSyntaxReference?.GetSyntax(token).GetLocation();
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

    /// <summary>
    /// Keeps contract identity diagnostics separate from the comparable source model.
    /// </summary>
    private sealed class ContractAnalysis(ContractIdentity identity, AnalysisResult<RpcContractModel> result)
    {
        /// <summary>
        /// Gets the identity used for compilation-wide duplicate detection.
        /// </summary>
        public ContractIdentity Identity { get; } = identity;

        /// <summary>
        /// Gets independently analyzed method metadata and diagnostics.
        /// </summary>
        public AnalysisResult<RpcContractModel> Result { get; } = result;
    }

    /// <summary>
    /// Contains only ID-check inputs and diagnostic locations, never a contract symbol.
    /// </summary>
    private sealed class ContractIdentity(string name, string shortName, ushort? id, Location? attributeLocation, Location? typeLocation)
    {
        /// <summary>
        /// Gets the fully qualified contract identity.
        /// </summary>
        public string Name { get; } = name;

        /// <summary>
        /// Gets the name used in invalid-ID diagnostics.
        /// </summary>
        public string ShortName { get; } = shortName;

        /// <summary>
        /// Gets the declared ID, or null while its argument cannot be resolved.
        /// </summary>
        public ushort? Id { get; } = id;

        /// <summary>
        /// Gets the location for a conflicting declaration.
        /// </summary>
        public Location? AttributeLocation { get; } = attributeLocation;

        /// <summary>
        /// Gets the location used when another declaration conflicts with this first declaration.
        /// </summary>
        public Location? TypeLocation { get; } = typeLocation;
    }
}