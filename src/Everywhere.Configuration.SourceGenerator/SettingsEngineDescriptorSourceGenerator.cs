using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Everywhere.Configuration.SourceGenerator;

/// <summary>
/// Generates SettingsEngine descriptor metadata for the application settings graph.
/// </summary>
/// <remarks>
/// The generated provider mirrors <c>ReflectionSettingsDescriptorProvider</c>
/// metadata without emitting reflection-based property accessors. It is only
/// produced for the assembly that owns <c>Everywhere.Configuration.Settings</c>.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class SettingsEngineDescriptorSourceGenerator : IIncrementalGenerator
{
    private const string EngineNamespace = "Everywhere.Configuration.Engine";
    private const string EngineGlobalPrefix = "global::Everywhere.Configuration.Engine";
    private const string ConfigurationGlobalPrefix = "global::Everywhere.Configuration";

    private static readonly SymbolDisplayFormat TypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat ValueTypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterSourceOutput(
            context.CompilationProvider,
            static (spc, compilation) => Emit(spc, compilation));
    }

    private static void Emit(SourceProductionContext ctx, Compilation compilation)
    {
        var settingsType = compilation.GetTypeByMetadataName("Everywhere.Configuration.Settings");
        if (settingsType is null || !settingsType.Locations.Any(static location => location.IsInSource))
        {
            return;
        }

        if (compilation.GetTypeByMetadataName("Everywhere.Configuration.Engine.ISettingsDescriptorProvider") is null)
        {
            return;
        }

        var models = BuildModels(compilation, settingsType);
        if (models.Length == 0)
        {
            return;
        }

        ctx.AddSource("Settings.GeneratedDescriptorProvider.g.cs", GenerateSource(models));
    }

    private static ImmutableArray<TypeModel> BuildModels(Compilation compilation, INamedTypeSymbol settingsType)
    {
        var queue = new Queue<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var models = new List<TypeModel>();

        Enqueue(settingsType);

        // Walk the descriptor graph from Settings instead of scanning the whole
        // compilation. Direct complex properties, collection element types, and
        // dictionary value types are enough for object patching and observed
        // path write-back, while unrelated app model types stay out of the
        // generated provider.
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            var properties = GetSettingsProperties(type)
                .Select(property => BuildPropertyModel(compilation, property))
                .OfType<PropertyModel>()
                .ToImmutableArray();

            var model = new TypeModel(
                type,
                GetCreateMethodName(models.Count, type),
                GetConstructorKind(type),
                GetUnknownMemberHandling(type, null),
                HasAttribute(type, KnownAttributes.SettingsSerializedSubtree),
                properties);

            models.Add(model);

            foreach (var property in properties)
            {
                if (property.ChildType is not null)
                {
                    Enqueue(property.ChildType);
                }

                var indexedType = property.Kind is "Array" or "List" ? property.ElementType
                    : property.Kind == "Dictionary" ? property.DictionaryValueType
                    : null;

                if (indexedType is INamedTypeSymbol namedIndexedType &&
                    CanPatchAsObject(compilation, namedIndexedType) &&
                    property.Kind != "SerializedSubtree" &&
                    !HasAttribute(namedIndexedType, KnownAttributes.SettingsSerializedSubtree))
                {
                    Enqueue(namedIndexedType);
                }
            }
        }

        return [..models];

        void Enqueue(INamedTypeSymbol type)
        {
            type = UnwrapNullable(type) as INamedTypeSymbol ?? type;
            if (type.TypeKind == TypeKind.Error)
            {
                return;
            }

            if (seen.Add(type))
            {
                queue.Enqueue(type);
            }
        }
    }

    private static IEnumerable<IPropertySymbol> GetSettingsProperties(INamedTypeSymbol type)
    {
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.SpecialType == SpecialType.System_Object)
            {
                yield break;
            }

            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (!seenNames.Add(property.Name))
                {
                    continue;
                }

                if (property.IsStatic || property.IsImplicitlyDeclared || property.GetMethod is null)
                {
                    continue;
                }

                if (property.Parameters.Length != 0)
                {
                    continue;
                }

                if (!IsAccessibleFromGeneratedCode(property.GetMethod.DeclaredAccessibility))
                {
                    continue;
                }

                if (!ShouldIncludeProperty(property))
                {
                    continue;
                }

                yield return property;
            }
        }
    }

    private static PropertyModel? BuildPropertyModel(Compilation compilation, IPropertySymbol property)
    {
        if (property.Type.TypeKind == TypeKind.Error)
        {
            return null;
        }

        var propertyType = property.Type;
        var jsonName = GetJsonName(property);
        var isSerializedSubtree =
            HasAttribute(property, KnownAttributes.SettingsSerializedSubtree) ||
            HasAttribute(UnwrapNullable(propertyType), KnownAttributes.SettingsSerializedSubtree);
        var isArray = propertyType is IArrayTypeSymbol;
        var isDictionary = TryGetDictionaryTypes(propertyType, out var dictionaryKeyType, out var dictionaryValueType);
        ITypeSymbol? elementType = null;
        var isList = !isArray && !isDictionary && TryGetListElementType(propertyType, out elementType);
        var kind =
            isSerializedSubtree ? "SerializedSubtree" :
            IsScalarType(compilation, propertyType) ? "Scalar" :
            isArray ? "Array" :
            isDictionary ? "Dictionary" :
            isList ? "List" :
            "Object";

        if (propertyType is IArrayTypeSymbol arrayType)
        {
            elementType = arrayType.ElementType;
        }
        else if (isDictionary)
        {
            elementType = dictionaryValueType;
        }

        INamedTypeSymbol? childType = null;
        if (kind == "Object")
        {
            var normalizedType = UnwrapNullable(propertyType);
            if (normalizedType is INamedTypeSymbol namedType && CanPatchAsObject(compilation, namedType))
            {
                childType = namedType;
            }
            else if (!CanWriteFromGeneratedCode(property))
            {
                return null;
            }
        }

        if (!CanWriteFromGeneratedCode(property) &&
            childType is null &&
            kind is not ("List" or "Dictionary"))
        {
            return null;
        }

        return new PropertyModel(
            property.ContainingType,
            property.Name,
            jsonName,
            propertyType,
            CanWriteFromGeneratedCode(property),
            kind,
            elementType,
            dictionaryKeyType,
            dictionaryValueType,
            GetUnknownMemberHandling(property, propertyType),
            childType);
    }

    private static string GenerateSource(ImmutableArray<TypeModel> models)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.Append("namespace ").Append(EngineNamespace).AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("internal static partial class SettingsDescriptorProviderFactory");
        sb.AppendLine("{");
        sb.Append("    static partial void TryCreateGenerated(ref ")
            .Append(EngineGlobalPrefix)
            .AppendLine(".ISettingsDescriptorProvider? provider)");
        sb.AppendLine("    {");
        sb.AppendLine("        provider = new GeneratedSettingsDescriptorProvider();");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.Append("internal sealed class GeneratedSettingsDescriptorProvider : ")
            .Append(EngineGlobalPrefix)
            .AppendLine(".ISettingsDescriptorProvider");
        sb.AppendLine("{");
        sb.AppendLine("    private readonly global::System.Threading.Lock _gate = new();");
        sb.Append("    private readonly global::System.Collections.Generic.Dictionary<global::System.Type, ")
            .Append(EngineGlobalPrefix)
            .AppendLine(".ISettingsDescriptor> _cache = new();");
        sb.Append("    private readonly ")
            .Append(EngineGlobalPrefix)
            .AppendLine(".ReflectionSettingsDescriptorProvider _fallback = new();");
        sb.AppendLine();
        sb.AppendLine("    private static T CastPropertyValue<T>(object? value)");
        sb.AppendLine("    {");
        sb.AppendLine("        // The binder has already validated JSON nullability before invoking generated setters.");
        sb.AppendLine("        // Keeping the cast in one helper avoids repeating null-forgiving casts in every emitted property.");
        sb.AppendLine("        return value is null ? default! : (T)value;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    // Generated descriptors cover the known Settings graph; the fallback keeps");
        sb.AppendLine("    // tests and future extension types working until the generator learns them.");
        sb.Append("    public ")
            .Append(EngineGlobalPrefix)
            .AppendLine(".ISettingsDescriptor GetDescriptor(global::System.Type type)");
        sb.AppendLine("    {");
        sb.AppendLine("        lock (_gate)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (_cache.TryGetValue(type, out var descriptor))");
        sb.AppendLine("            {");
        sb.AppendLine("                return descriptor;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            descriptor = CreateDescriptor(type);");
        sb.AppendLine("            _cache.Add(type, descriptor);");
        sb.AppendLine("            return descriptor;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.Append("    private ")
            .Append(EngineGlobalPrefix)
            .AppendLine(".ISettingsDescriptor CreateDescriptor(global::System.Type type)");
        sb.AppendLine("    {");

        foreach (var model in models)
        {
            sb.Append("        if (type == typeof(").Append(FormatType(model.Type)).AppendLine("))");
            sb.AppendLine("        {");
            sb.Append("            return ").Append(model.CreateMethodName).AppendLine("();");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("        return _fallback.GetDescriptor(type);");
        sb.AppendLine("    }");

        foreach (var model in models)
        {
            sb.AppendLine();
            EmitCreateMethod(sb, model);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void EmitCreateMethod(StringBuilder sb, TypeModel model)
    {
        sb.Append("    private ")
            .Append(EngineGlobalPrefix)
            .Append(".ISettingsDescriptor ")
            .Append(model.CreateMethodName)
            .AppendLine("()");
        sb.AppendLine("    {");
        sb.Append("        return new ")
            .Append(EngineGlobalPrefix)
            .AppendLine(".SettingsObjectDescriptor(");
        sb.Append("            typeof(").Append(FormatType(model.Type)).AppendLine("),");
        sb.Append("            new ")
            .Append(EngineGlobalPrefix)
            .AppendLine(".ISettingsPropertyDescriptor[]");
        sb.AppendLine("            {");

        foreach (var property in model.Properties)
        {
            EmitPropertyDescriptor(sb, property);
        }

        sb.AppendLine("            },");
        sb.Append("            ")
            .Append(ConfigurationGlobalPrefix)
            .Append(".SettingsUnknownMemberHandling.")
            .Append(model.UnknownMemberHandling)
            .AppendLine(",");
        sb.Append("            ").Append(ToBoolLiteral(model.IsSerializedSubtree)).AppendLine(",");
        sb.Append("            ").Append(GetInstanceFactoryExpression(model)).AppendLine(");");
        sb.AppendLine("    }");
    }

    private static void EmitPropertyDescriptor(StringBuilder sb, PropertyModel property)
    {
        var ownerType = FormatType(property.OwnerType);
        var propertyType = FormatType(property.PropertyType);
        var propertyValueType = FormatValueType(property.PropertyType);
        var setter = property.CanWrite ?
            $"static (instance, value) => (({ownerType})instance).{property.ClrName} = CastPropertyValue<{propertyValueType}>(value)" :
            "null";
        var childDescriptor = property.ChildType is null ? "null" : $"GetDescriptor(typeof({FormatType(property.ChildType)}))";

        sb.Append("                new ")
            .Append(EngineGlobalPrefix)
            .AppendLine(".DelegateSettingsPropertyDescriptor(");
        sb.Append("                    typeof(").Append(ownerType).AppendLine("),");
        sb.Append("                    \"").Append(EscapeStringForCode(property.ClrName)).AppendLine("\",");
        sb.Append("                    \"").Append(EscapeStringForCode(property.JsonName)).AppendLine("\",");
        sb.Append("                    typeof(").Append(propertyType).AppendLine("),");
        sb.Append("                    ").Append(ToBoolLiteral(property.CanWrite)).AppendLine(",");
        sb.Append("                    ")
            .Append(EngineGlobalPrefix)
            .Append(".SettingsPropertyKind.")
            .Append(property.Kind)
            .AppendLine(",");
        sb.Append("                    ").Append(FormatNullableTypeOf(property.ElementType)).AppendLine(",");
        sb.Append("                    ").Append(FormatNullableTypeOf(property.DictionaryKeyType)).AppendLine(",");
        sb.Append("                    ").Append(FormatNullableTypeOf(property.DictionaryValueType)).AppendLine(",");
        sb.Append("                    ")
            .Append(ConfigurationGlobalPrefix)
            .Append(".SettingsUnknownMemberHandling.")
            .Append(property.UnknownMemberHandling)
            .AppendLine(",");
        sb.Append("                    ").Append(childDescriptor).AppendLine(",");
        sb.Append("                    static instance => ((").Append(ownerType).Append(")instance).").Append(property.ClrName).AppendLine(",");
        sb.Append("                    ").Append(setter).AppendLine("),");
    }

    private static bool ShouldIncludeProperty(IPropertySymbol property)
    {
        var jsonIgnore = GetAttribute(property, KnownAttributes.JsonIgnore);
        if (jsonIgnore is null)
        {
            return true;
        }

        return GetEnumArgumentName(jsonIgnore.GetNamedArgument("Condition")) == "Never";
    }

    private static string GetInstanceFactoryExpression(TypeModel model) =>
        model.ConstructorKind switch
        {
            "Default" => $"static _ => new {FormatType(model.Type)}()",
            "ServiceProvider" => $"static serviceProvider => new {FormatType(model.Type)}(serviceProvider)",
            _ => "null"
        };

    private static bool CanWriteFromGeneratedCode(IPropertySymbol property) =>
        property.SetMethod is { IsStatic: false, IsInitOnly: false } setMethod &&
        IsAccessibleFromGeneratedCode(setMethod.DeclaredAccessibility);

    private static string GetConstructorKind(INamedTypeSymbol type)
    {
        if (type.IsAbstract)
        {
            return "None";
        }

        foreach (var constructor in type.InstanceConstructors)
        {
            if (!IsAccessibleFromGeneratedCode(constructor.DeclaredAccessibility))
            {
                continue;
            }

            if (constructor.Parameters.Length == 0)
            {
                return "Default";
            }

            if (constructor.Parameters.Length == 1 &&
                constructor.Parameters[0].Type.ToDisplayString() == "System.IServiceProvider")
            {
                return "ServiceProvider";
            }
        }

        return "None";
    }

    private static bool IsAccessibleFromGeneratedCode(Accessibility accessibility) =>
        accessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;

    private static bool CanPatchAsObject(Compilation compilation, ITypeSymbol type)
    {
        type = UnwrapNullable(type);
        if (type.TypeKind is TypeKind.Interface or TypeKind.TypeParameter)
        {
            return false;
        }

        if (type is INamedTypeSymbol { IsAbstract: true })
        {
            return false;
        }

        if (IsScalarType(compilation, type))
        {
            return false;
        }

        if (type is IArrayTypeSymbol ||
            TryGetListElementType(type, out _) ||
            TryGetDictionaryTypes(type, out _, out _))
        {
            return false;
        }

        if (InheritsFrom(type, "System.Delegate"))
        {
            return false;
        }

        return type is INamedTypeSymbol;
    }

    private static bool IsScalarType(Compilation compilation, ITypeSymbol type)
    {
        type = UnwrapNullable(type);

        if (type.TypeKind == TypeKind.Enum || type.IsValueType)
        {
            return true;
        }

        if (type.SpecialType == SpecialType.System_String)
        {
            return true;
        }

        var knownScalarNames = new[]
        {
            "System.Decimal",
            "System.Guid",
            "System.DateTime",
            "System.DateTimeOffset",
            "System.DateOnly",
            "System.TimeOnly",
            "System.TimeSpan",
            "System.Uri",
            "System.Object"
        };

        if (knownScalarNames.Any(name => IsType(type, compilation.GetTypeByMetadataName(name))))
        {
            return true;
        }

        return HasAttribute(type, KnownAttributes.TypeConverter);
    }

    private static bool TryGetListElementType(ITypeSymbol type, out ITypeSymbol? elementType)
    {
        type = UnwrapNullable(type);

        if (type is INamedTypeSymbol namedType &&
            IsGenericOriginalDefinition(namedType, "global::System.Collections.ObjectModel.ObservableCollection<T>"))
        {
            elementType = namedType.TypeArguments[0];
            return true;
        }

        var candidates = type is INamedTypeSymbol listCandidate ? type.AllInterfaces.Concat([listCandidate]) : type.AllInterfaces;

        // ReSharper disable once PossibleMultipleEnumeration
        var listType = candidates.FirstOrDefault(i => IsGenericOriginalDefinition(i, "global::System.Collections.Generic.IList<T>"));
        if (listType is not null)
        {
            elementType = listType.TypeArguments[0];
            return true;
        }

        // ReSharper disable once PossibleMultipleEnumeration
        var readOnlyListType = candidates.FirstOrDefault(i => IsGenericOriginalDefinition(i, "global::System.Collections.Generic.IReadOnlyList<T>"));
        if (readOnlyListType is not null)
        {
            elementType = readOnlyListType.TypeArguments[0];
            return true;
        }

        elementType = null;
        return false;
    }

    private static bool TryGetDictionaryTypes(ITypeSymbol type, out ITypeSymbol? keyType, out ITypeSymbol? valueType)
    {
        type = UnwrapNullable(type);

        var candidates = type is INamedTypeSymbol namedType ? type.AllInterfaces.Concat([namedType]) : type.AllInterfaces;

        var dictionaryType = candidates.FirstOrDefault(i =>
            IsGenericOriginalDefinition(i, "global::System.Collections.Generic.IDictionary<TKey, TValue>"));
        if (dictionaryType is not null)
        {
            keyType = dictionaryType.TypeArguments[0];
            valueType = dictionaryType.TypeArguments[1];
            return true;
        }

        keyType = null;
        valueType = null;
        return false;
    }

    private static string GetJsonName(IPropertySymbol property)
    {
        var jsonPropertyName = GetAttribute(property, KnownAttributes.JsonPropertyName);
        return jsonPropertyName?.ConstructorArguments.FirstOrDefault().Value as string ?? property.Name;
    }

    private static string GetUnknownMemberHandling(ISymbol symbol, ITypeSymbol? type)
    {
        if (GetAttribute(symbol, KnownAttributes.SettingsUnknownMemberHandling) is { } attribute &&
            GetEnumArgumentName(attribute.ConstructorArguments.FirstOrDefault()) is { } handling)
        {
            return handling;
        }

        if (type is not null)
        {
            type = UnwrapNullable(type);
            if (GetAttribute(type, KnownAttributes.SettingsUnknownMemberHandling) is { } typeAttribute &&
                GetEnumArgumentName(typeAttribute.ConstructorArguments.FirstOrDefault()) is { } typeHandling)
            {
                return typeHandling;
            }
        }

        return "Preserve";
    }

    private static string? GetEnumArgumentName(TypedConstant constant)
    {
        if (constant.IsNull || constant.Type is not INamedTypeSymbol enumType)
        {
            return null;
        }

        foreach (var field in enumType.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.HasConstantValue && Equals(field.ConstantValue, constant.Value))
            {
                return field.Name;
            }
        }

        return null;
    }

    private static string? GetEnumArgumentName(TypedConstant? constant) =>
        constant is { } value ? GetEnumArgumentName(value) : null;

    private static bool HasAttribute(ISymbol symbol, string fullMetadataName) =>
        GetAttribute(symbol, fullMetadataName) is not null;

    private static AttributeData? GetAttribute(ISymbol symbol, string fullMetadataName) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == fullMetadataName);

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments.Length: 1 } namedType)
        {
            return namedType.TypeArguments[0];
        }

        return type;
    }

    private static bool InheritsFrom(ITypeSymbol type, string metadataName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == metadataName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsType(ITypeSymbol type, ITypeSymbol? knownType) =>
        knownType is not null && SymbolEqualityComparer.Default.Equals(type, knownType);

    private static bool IsGenericOriginalDefinition(INamedTypeSymbol type, string fullyQualifiedOriginalDefinition) =>
        type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == fullyQualifiedOriginalDefinition;

    private static string FormatType(ITypeSymbol type) => UnwrapNullableReference(type).ToDisplayString(TypeFormat);

    private static string FormatValueType(ITypeSymbol type) => type.ToDisplayString(ValueTypeFormat);

    private static ITypeSymbol UnwrapNullableReference(ITypeSymbol type) => type;

    private static string FormatNullableTypeOf(ITypeSymbol? type) =>
        type is null ? "null" : $"typeof({FormatType(type)})";

    private static string GetCreateMethodName(int index, INamedTypeSymbol type)
    {
        var builder = new StringBuilder("CreateDescriptor_");
        builder.Append(index.ToString(CultureInfo.InvariantCulture));
        builder.Append('_');

        foreach (var c in type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return builder.ToString();
    }

    private static string EscapeStringForCode(string value) =>
        value
            .Replace("\\", @"\\")
            .Replace("\"", @"\""")
            .Replace("\r", @"\r")
            .Replace("\n", @"\n")
            .Replace("\t", @"\t");

    private static string ToBoolLiteral(bool value) => value ? "true" : "false";

    private sealed record TypeModel(
        INamedTypeSymbol Type,
        string CreateMethodName,
        string ConstructorKind,
        string UnknownMemberHandling,
        bool IsSerializedSubtree,
        ImmutableArray<PropertyModel> Properties
    );

    private sealed record PropertyModel(
        INamedTypeSymbol OwnerType,
        string ClrName,
        string JsonName,
        ITypeSymbol PropertyType,
        bool CanWrite,
        string Kind,
        ITypeSymbol? ElementType,
        ITypeSymbol? DictionaryKeyType,
        ITypeSymbol? DictionaryValueType,
        string UnknownMemberHandling,
        INamedTypeSymbol? ChildType
    );
}