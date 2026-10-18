using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Threading;
using Everywhere.SourceGeneration;
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

    private static readonly PropertyFlags[] DefinedPropertyFlags =
    [
        PropertyFlags.CanWrite,
        PropertyFlags.CanInitialize,
        PropertyFlags.AllowsNull,
        PropertyFlags.ElementAllowsNull
    ];

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.CompilationProvider.Select(static (compilation, token) =>
        {
            if (compilation.GetTypeByMetadataName("Everywhere.Configuration.Engine.ISettingsDescriptorProvider") is null ||
                compilation.GetTypeByMetadataName("Everywhere.Configuration.Settings") is not { } root ||
                !root.Locations.Any(static location => location.IsInSource))
            {
                return null;
            }

            return new DescriptorModel(BuildModels(compilation, root, token));
        }).WithTrackingName("HostDescriptorModel");

        var sources = models.Select(static (model, token) => model is null ? null : GenerateSource(model.Types, token))
            .WithTrackingName("HostDescriptorSource");
        context.RegisterSourceOutput(sources, static (production, source) =>
        {
            if (source is not null)
            {
                production.AddSource("Settings.GeneratedDescriptorProvider.g.cs", source);
            }
        });
    }

    private static EquatableArray<TypeModel> BuildModels(Compilation compilation, INamedTypeSymbol settingsType, CancellationToken token)
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
            token.ThrowIfCancellationRequested();
            var type = queue.Dequeue();
            var properties = GetSettingsProperties(type)
                .Select(property =>
                {
                    token.ThrowIfCancellationRequested();
                    return BuildPropertyModel(compilation, property, Enqueue);
                })
                .OfType<PropertyModel>()
                .ToImmutableArray();

            var model = new TypeModel(
                FormatType(type),
                GetCreateMethodName(models.Count, type),
                GetConstructorKind(type),
                GetUnknownMemberHandling(type, null),
                HasAttribute(type, KnownAttributes.SettingsSerializedSubtree),
                new EquatableArray<PropertyModel>(properties));

            models.Add(model);
        }

        return new EquatableArray<TypeModel>(models.ToImmutableArray());

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

    private static PropertyModel? BuildPropertyModel(Compilation compilation, IPropertySymbol property, Action<INamedTypeSymbol> enqueue)
    {
        if (property.Type.TypeKind == TypeKind.Error)
        {
            return null;
        }

        var propertyType = property.Type;
        var flags = PropertyFlags.None;
        if (CanWriteFromGeneratedCode(property))
        {
            flags |= PropertyFlags.CanWrite;
        }

        if (CanInitializeFromGeneratedCode(property))
        {
            flags |= PropertyFlags.CanInitialize;
        }

        if (AllowsNull(property))
        {
            flags |= PropertyFlags.AllowsNull;
        }

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

        if (elementType is not null && AllowsNull(elementType))
        {
            flags |= PropertyFlags.ElementAllowsNull;
        }

        var canAssign = flags.HasFlag(PropertyFlags.CanWrite) || flags.HasFlag(PropertyFlags.CanInitialize);
        INamedTypeSymbol? childType = null;
        if (kind == "Object")
        {
            var normalizedType = UnwrapNullable(propertyType);
            if (normalizedType is INamedTypeSymbol namedType && CanPatchAsObject(compilation, namedType))
            {
                childType = namedType;
            }
            else if (!canAssign)
            {
                return null;
            }
        }

        if (!canAssign &&
            childType is null &&
            kind is not ("List" or "Dictionary"))
        {
            return null;
        }

        if (childType is not null)
        {
            enqueue(childType);
        }

        var indexedType = kind is "Array" or "List" ? elementType : kind == "Dictionary" ? dictionaryValueType : null;
        if (indexedType is INamedTypeSymbol namedIndexedType &&
            (HasAttribute(namedIndexedType, KnownAttributes.SettingsSerializedSubtree) || CanPatchAsObject(compilation, namedIndexedType)))
        {
            enqueue(namedIndexedType);
        }

        return new PropertyModel(
            FormatType(property.ContainingType),
            property.Name,
            jsonName,
            FormatType(propertyType),
            FormatValueType(propertyType),
            kind,
            flags,
            elementType is null ? null : FormatType(elementType),
            dictionaryKeyType is null ? null : FormatType(dictionaryKeyType),
            dictionaryValueType is null ? null : FormatType(dictionaryValueType),
            GetUnknownMemberHandling(property, propertyType),
            childType is null ? null : FormatType(childType));
    }

    private static string GenerateSource(EquatableArray<TypeModel> models, CancellationToken token)
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
            token.ThrowIfCancellationRequested();
            sb.Append("        if (type == typeof(").Append(model.Type).AppendLine("))");
            sb.AppendLine("        {");
            sb.Append("            return ").Append(model.CreateMethodName).AppendLine("();");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("        return _fallback.GetDescriptor(type);");
        sb.AppendLine("    }");

        foreach (var model in models)
        {
            token.ThrowIfCancellationRequested();
            sb.AppendLine();
            EmitCreateMethod(sb, model);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void EmitCreateMethod(StringBuilder sb, TypeModel model)
    {
        var descriptorClassName = GetDescriptorClassName(model);

        sb.Append("    private ")
            .Append(EngineGlobalPrefix)
            .Append(".ISettingsDescriptor ")
            .Append(model.CreateMethodName)
            .AppendLine("()");
        sb.AppendLine("    {");
        sb.Append("        return new ").Append(descriptorClassName).AppendLine("(");
        sb.Append("            new ")
            .Append(EngineGlobalPrefix)
            .AppendLine(".ISettingsPropertyDescriptor[]");
        sb.AppendLine("            {");

        foreach (var property in model.Properties)
        {
            EmitPropertyDescriptor(sb, property);
        }

        sb.AppendLine("            });");
        sb.AppendLine("    }");

        if (model.ConstructorKind != ConstructorKind.None &&
            model.Properties.Any(static property => property.Flags.HasFlag(PropertyFlags.CanInitialize)))
        {
            sb.AppendLine();
            EmitCreateInstanceMethod(sb, model);
        }

        sb.AppendLine();
        EmitDescriptorClass(sb, model);
    }

    private static void EmitPropertyDescriptor(StringBuilder sb, PropertyModel property)
    {
        var ownerType = property.OwnerType;
        var propertyType = property.PropertyType;
        var propertyValueType = property.PropertyValueType;
        var setter = property.Flags.HasFlag(PropertyFlags.CanWrite) ?
            $"static (instance, value) => (({ownerType})instance).{property.ClrName} = CastPropertyValue<{propertyValueType}>(value)" :
            "null";
        var childDescriptor = property.ChildType is null ? "null" : $"GetDescriptor(typeof({property.ChildType}))";
        var dictionaryKeyReader = property.DictionaryKeyType is null ?
            "null" :
            $"static keyText => {EngineGlobalPrefix}.DictionaryKeyReader<{property.DictionaryKeyType}>.ReadObject(keyText)";

        sb.Append("                new ")
            .Append(EngineGlobalPrefix)
            .AppendLine(".DelegateSettingsPropertyDescriptor(");
        sb.Append("                    typeof(").Append(ownerType).AppendLine("),");
        sb.Append("                    \"").Append(EscapeStringForCode(property.ClrName)).AppendLine("\",");
        sb.Append("                    \"").Append(EscapeStringForCode(property.JsonName)).AppendLine("\",");
        sb.Append("                    typeof(").Append(propertyType).AppendLine("),");
        sb.Append("                    ")
            .Append(EngineGlobalPrefix)
            .Append(".SettingsPropertyKind.")
            .Append(property.Kind)
            .AppendLine(",");
        sb.Append("                    ").Append(FormatPropertyFlags(property.Flags)).AppendLine(",");
        sb.Append("                    ").Append(FormatNullableTypeOf(property.ElementType)).AppendLine(",");
        sb.Append("                    ").Append(FormatNullableTypeOf(property.DictionaryKeyType)).AppendLine(",");
        sb.Append("                    ").Append(FormatNullableTypeOf(property.DictionaryValueType)).AppendLine(",");
        sb.Append("                    ").Append(dictionaryKeyReader).AppendLine(",");
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

    private static void EmitDescriptorClass(StringBuilder sb, TypeModel model)
    {
        sb.Append("    private sealed class ").Append(GetDescriptorClassName(model)).Append(" : ")
            .Append(EngineGlobalPrefix)
            .AppendLine(".SettingsObjectDescriptor");
        sb.AppendLine("    {");
        sb.Append("        public ").Append(GetDescriptorClassName(model)).AppendLine("(");
        sb.Append("            global::System.Collections.Generic.IReadOnlyList<")
            .Append(EngineGlobalPrefix)
            .AppendLine(".ISettingsPropertyDescriptor> properties)");
        sb.AppendLine("            : base(");
        sb.Append("                typeof(").Append(model.Type).AppendLine("),");
        sb.AppendLine("                properties,");
        sb.Append("                ")
            .Append(ConfigurationGlobalPrefix)
            .Append(".SettingsUnknownMemberHandling.")
            .Append(model.UnknownMemberHandling)
            .AppendLine(",");
        sb.Append("                ").Append(ToBoolLiteral(model.IsSerializedSubtree)).AppendLine(")");
        sb.AppendLine("        {");
        sb.AppendLine("        }");

        if (model.ConstructorKind == ConstructorKind.Default)
        {
            sb.AppendLine();
            EmitTryCreateInstanceOverride(sb, model);
        }

        sb.AppendLine("    }");
    }

    private static void EmitTryCreateInstanceOverride(StringBuilder sb, TypeModel model)
    {
        sb.AppendLine("        public override bool TryCreateInstance(");
        sb.AppendLine("            global::System.Collections.Generic.IReadOnlyDictionary<string, object?>? initialValues,");
        sb.AppendLine("            out object? instance)");
        sb.AppendLine("        {");
        sb.AppendLine("            try");
        sb.AppendLine("            {");

        if (model.Properties.Any(static property => property.Flags.HasFlag(PropertyFlags.CanInitialize)))
        {
            sb.Append("                instance = ").Append(GetCreateInstanceMethodName(model)).AppendLine("(initialValues);");
            sb.AppendLine("                return instance is not null;");
        }
        else
        {
            sb.Append("                instance = new ").Append(model.Type).AppendLine("();");
            sb.AppendLine("                return true;");
        }

        sb.AppendLine("            }");
        sb.AppendLine("            catch");
        sb.AppendLine("            {");
        sb.AppendLine("                instance = null;");
        sb.AppendLine("                return false;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
    }

    private static void EmitCreateInstanceMethod(StringBuilder sb, TypeModel model)
    {
        var properties = model.Properties
            .Where(static property => property.Flags.HasFlag(PropertyFlags.CanInitialize))
            .ToImmutableArray();

        sb.Append("    private static object? ")
            .Append(GetCreateInstanceMethodName(model))
            .AppendLine("(");
        sb.AppendLine("        global::System.Collections.Generic.IReadOnlyDictionary<string, object?>? initialValues)");
        sb.AppendLine("    {");

        if (properties.Length == 1)
        {
            EmitSingleInitializerCreateInstanceMethodBody(sb, model, properties[0]);
            sb.AppendLine("    }");
            return;
        }

        for (var i = 0; i < properties.Length; i++)
        {
            sb.Append("        object? initValue_").Append(i.ToString(CultureInfo.InvariantCulture)).AppendLine(" = null;");
        }

        sb.AppendLine("        // Object initializers need compile-time member assignments; the mask selects");
        sb.AppendLine("        // the exact initializer shape for the JSON properties that were present.");
        sb.AppendLine("        var initializeMask = 0;");

        for (var i = 0; i < properties.Length; i++)
        {
            var property = properties[i];
            var mask = 1 << i;
            sb.Append("        if (initialValues?.TryGetValue(\"")
                .Append(EscapeStringForCode(property.JsonName))
                .Append("\", out initValue_")
                .Append(i.ToString(CultureInfo.InvariantCulture))
                .AppendLine(") is true)");
            sb.AppendLine("        {");
            sb.Append("            initializeMask |= ").Append(mask.ToString(CultureInfo.InvariantCulture)).AppendLine(";");
            sb.AppendLine("        }");
        }

        sb.AppendLine();
        sb.AppendLine("        return initializeMask switch");
        sb.AppendLine("        {");

        var combinationCount = 1 << properties.Length;
        for (var mask = 0; mask < combinationCount; mask++)
        {
            sb.Append("            ").Append(mask.ToString(CultureInfo.InvariantCulture)).Append(" => ");
            EmitCreateInstanceExpression(sb, model, properties, mask);
            sb.AppendLine(",");
        }

        sb.Append("            _ => ").Append(GetConstructorExpression(model)).AppendLine(",");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
    }

    private static void EmitSingleInitializerCreateInstanceMethodBody(
        StringBuilder sb,
        TypeModel model,
        PropertyModel property)
    {
        sb.Append("        if (initialValues?.TryGetValue(\"")
            .Append(EscapeStringForCode(property.JsonName))
            .AppendLine("\", out var initValue) is true)");
        sb.AppendLine("        {");
        sb.Append("            return ")
            .Append(GetConstructorExpression(model))
            .AppendLine();
        sb.AppendLine("            {");
        sb.Append("                ")
            .Append(property.ClrName)
            .Append(" = CastPropertyValue<")
            .Append(property.PropertyValueType)
            .AppendLine(">(initValue),");
        sb.AppendLine("            };");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.Append("        return ").Append(GetConstructorExpression(model)).AppendLine(";");
    }

    private static void EmitCreateInstanceExpression(
        StringBuilder sb,
        TypeModel model,
        ImmutableArray<PropertyModel> properties,
        int mask)
    {
        sb.Append(GetConstructorExpression(model));

        if (mask == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("            {");

        for (var i = 0; i < properties.Length; i++)
        {
            if ((mask & (1 << i)) == 0)
            {
                continue;
            }

            var property = properties[i];
            sb.Append("                ")
                .Append(property.ClrName)
                .Append(" = CastPropertyValue<")
                .Append(property.PropertyValueType)
                .Append(">(initValue_")
                .Append(i.ToString(CultureInfo.InvariantCulture))
                .AppendLine("),");
        }

        sb.Append("            }");
    }

    private static string GetConstructorExpression(TypeModel model) =>
        model.ConstructorKind switch
        {
            ConstructorKind.Default => $"new {model.Type}()",
            _ => "null"
        };

    private static string GetCreateInstanceMethodName(TypeModel model) =>
        model.CreateMethodName + "_Instance";

    private static string GetDescriptorClassName(TypeModel model) =>
        model.CreateMethodName + "_Descriptor";

    private static bool CanWriteFromGeneratedCode(IPropertySymbol property) =>
        property.SetMethod is { IsStatic: false, IsInitOnly: false } setMethod &&
        IsAccessibleFromGeneratedCode(setMethod.DeclaredAccessibility);

    private static bool CanInitializeFromGeneratedCode(IPropertySymbol property) =>
        property.SetMethod is { IsStatic: false, IsInitOnly: true } setMethod &&
        IsAccessibleFromGeneratedCode(setMethod.DeclaredAccessibility);

    private static bool AllowsNull(IPropertySymbol property)
    {
        if (!CanTypeAcceptNull(property.Type))
        {
            return false;
        }

        if (HasAttribute(property, KnownAttributes.AllowNull))
        {
            return true;
        }

        if (HasAttribute(property, KnownAttributes.DisallowNull))
        {
            return false;
        }

        return AllowsNull(property.Type);
    }

    private static bool AllowsNull(ITypeSymbol type)
    {
        if (!CanTypeAcceptNull(type))
        {
            return false;
        }

        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
        {
            return true;
        }

        if (type is ITypeParameterSymbol { HasNotNullConstraint: true })
        {
            return false;
        }

        // Oblivious reference types are treated as nullable, matching STJ's
        // JsonPropertyInfo defaults when no non-null annotation is available.
        return type.NullableAnnotation is not NullableAnnotation.NotAnnotated;
    }

    private static bool CanTypeAcceptNull(ITypeSymbol type) =>
        !type.IsValueType ||
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private static ConstructorKind GetConstructorKind(INamedTypeSymbol type)
    {
        if (type.IsAbstract)
        {
            return ConstructorKind.None;
        }

        foreach (var constructor in type.InstanceConstructors)
        {
            if (!IsAccessibleFromGeneratedCode(constructor.DeclaredAccessibility))
            {
                continue;
            }

            if (constructor.Parameters.Length == 0)
            {
                return ConstructorKind.Default;
            }
        }

        return ConstructorKind.None;
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

    private static string FormatNullableTypeOf(string? type) =>
        type is null ? "null" : $"typeof({type})";

    private static string FormatPropertyFlags(PropertyFlags flags)
    {
        if (flags == PropertyFlags.None)
        {
            return $"{EngineGlobalPrefix}.SettingsPropertyFlags.None";
        }

        var names = ImmutableArray.CreateBuilder<string>();
        foreach (var flag in DefinedPropertyFlags)
        {
            if (!flags.HasFlag(flag))
            {
                continue;
            }

            names.Add($"{EngineGlobalPrefix}.SettingsPropertyFlags.{flag}");
        }

        return string.Join(" | ", names);
    }

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

    private enum ConstructorKind
    {
        None,
        Default
    }

    [Flags]
    private enum PropertyFlags
    {
        None = 0,
        CanWrite = 1 << 0,
        CanInitialize = 1 << 1,
        AllowsNull = 1 << 2,
        ElementAllowsNull = 1 << 3
    }

    /// <summary>
    /// Describes one reachable settings type without retaining its Roslyn symbol.
    /// </summary>
    private sealed record TypeModel(
        string Type,
        string CreateMethodName,
        ConstructorKind ConstructorKind,
        string UnknownMemberHandling,
        bool IsSerializedSubtree,
        EquatableArray<PropertyModel> Properties
    );

    /// <summary>
    /// Contains the resolved names and patching capabilities used by a property descriptor.
    /// </summary>
    private sealed record PropertyModel(
        string OwnerType,
        string ClrName,
        string JsonName,
        string PropertyType,
        string PropertyValueType,
        string Kind,
        PropertyFlags Flags,
        string? ElementType,
        string? DictionaryKeyType,
        string? DictionaryValueType,
        string UnknownMemberHandling,
        string? ChildType
    );

    /// <summary>
    /// Compares the complete reachable settings graph before formatting the host provider.
    /// </summary>
    private sealed record DescriptorModel(EquatableArray<TypeModel> Types);
}
