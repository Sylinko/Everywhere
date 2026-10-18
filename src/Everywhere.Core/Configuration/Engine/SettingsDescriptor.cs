using System.Text.Json;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Serialization;
using ZLinq;

namespace Everywhere.Configuration.Engine;

/// <summary>
/// Describes a settings object type and the JSON members known to the settings engine.
/// </summary>
public interface ISettingsDescriptor
{
    /// <summary>
    /// Gets the CLR type described by this descriptor.
    /// </summary>
    Type Type { get; }

    /// <summary>
    /// Gets the settings properties that are persisted by this descriptor.
    /// </summary>
    IReadOnlyList<ISettingsPropertyDescriptor> Properties { get; }

    /// <summary>
    /// Gets the policy used when the backing JSON object contains keys not represented by <see cref="Properties"/>.
    /// </summary>
    SettingsUnknownMemberHandling UnknownMemberHandling { get; }

    /// <summary>
    /// Gets whether this type is a terminal System.Text.Json subtree.
    /// </summary>
    bool IsSerializedSubtree { get; }

    /// <summary>
    /// Attempts to create a new instance of <see cref="Type"/>.
    /// </summary>
    /// <remarks>
    /// Generated descriptors call an accessible constructor directly. The
    /// reflection fallback may still use reflection here, but the binder does
    /// not need to know which strategy produced the instance.
    /// </remarks>
    bool TryCreateInstance(IReadOnlyDictionary<string, object?>? initialValues, out object? instance);

    /// <summary>
    /// Finds a property by CLR property name or JSON property name.
    /// </summary>
    ISettingsPropertyDescriptor? FindProperty(string clrOrJsonName);
}

/// <summary>
/// Provides descriptor metadata for settings-reachable CLR types.
/// </summary>
/// <remarks>
/// SettingsEngine consumes this abstraction so the normal runtime can use
/// generated descriptors, while tests and unsupported shapes can still fall
/// back to reflection descriptors. The descriptor provider describes JSON
/// document patching only; it does not expose <c>IConfiguration</c> keys or
/// string-only values.
/// </remarks>
public interface ISettingsDescriptorProvider
{
    /// <summary>
    /// Gets the descriptor for a CLR settings type.
    /// </summary>
    ISettingsDescriptor GetDescriptor(Type type);
}

/// <summary>
/// Describes the single binding shape used for a settings property.
/// </summary>
/// <remarks>
/// These values are deliberately mutually exclusive. A property may be a CLR
/// collection, a JSON-serialized terminal subtree, a scalar, or a patchable
/// object, but the binder should never have to reconcile multiple shape flags.
/// </remarks>
public enum SettingsPropertyKind
{
    /// <summary>
    /// A scalar value handled by System.Text.Json as a single JSON value.
    /// </summary>
    Scalar,

    /// <summary>
    /// A complex object that can be patched member-by-member.
    /// </summary>
    Object,

    /// <summary>
    /// A CLR array that is replaced as a whole.
    /// </summary>
    Array,

    /// <summary>
    /// A mutable or replaceable list-like collection.
    /// </summary>
    List,

    /// <summary>
    /// A dictionary stored as a JSON object.
    /// </summary>
    Dictionary,

    /// <summary>
    /// A terminal subtree that is read and written through System.Text.Json without member patching.
    /// </summary>
    SerializedSubtree
}

/// <summary>
/// Describes the independent capabilities and input constraints of a settings property.
/// </summary>
[Flags]
public enum SettingsPropertyFlags
{
    /// <summary>
    /// The property has no optional capabilities or constraints.
    /// </summary>
    None = 0,

    /// <summary>
    /// The property can be replaced through its setter.
    /// </summary>
    CanWrite = 1 << 0,

    /// <summary>
    /// The property can be assigned while a new object is being created.
    /// </summary>
    /// <remarks>
    /// This mirrors Microsoft Binder's <c>SetOnInit</c> concept. Init-only
    /// properties are not ordinary patch setters, but SettingsEngine may bind
    /// them while creating a new runtime object during startup load.
    /// </remarks>
    CanInitialize = 1 << 1,

    /// <summary>
    /// The property accepts an explicit JSON <see langword="null"/> value.
    /// </summary>
    /// <remarks>
    /// This is the SettingsEngine equivalent of <c>JsonPropertyInfo.IsSetNullable</c>:
    /// it describes the property's input contract, including nullable reference
    /// annotations and <see cref="System.Diagnostics.CodeAnalysis.AllowNullAttribute"/>
    /// or <see cref="System.Diagnostics.CodeAnalysis.DisallowNullAttribute"/>.
    /// </remarks>
    AllowsNull = 1 << 2,

    /// <summary>
    /// Array/list elements or dictionary values accept JSON <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Generic element nullability is retained by the SettingsEngine source
    /// generator because it is not represented by the runtime <see cref="Type"/>
    /// alone and is not fully enforced by System.Text.Json.
    /// </remarks>
    ElementAllowsNull = 1 << 3
}

/// <summary>
/// Describes one settings property and how it maps to JSON.
/// </summary>
public interface ISettingsPropertyDescriptor
{
    /// <summary>
    /// Gets the CLR property name.
    /// </summary>
    string ClrName { get; }

    /// <summary>
    /// Gets the JSON property name after applying <see cref="JsonPropertyNameAttribute"/>.
    /// </summary>
    string JsonName { get; }

    /// <summary>
    /// Gets the CLR property type.
    /// </summary>
    Type PropertyType { get; }

    /// <summary>
    /// Gets the mutually exclusive binding shape for this property.
    /// </summary>
    SettingsPropertyKind Kind { get; }

    /// <summary>
    /// Gets the property's independent capabilities and input constraints.
    /// </summary>
    SettingsPropertyFlags Flags { get; }

    /// <summary>
    /// Gets the element type for arrays, lists, or dictionary values.
    /// </summary>
    Type? ElementType { get; }

    /// <summary>
    /// Gets the dictionary key type when this property is dictionary-shaped.
    /// </summary>
    Type? DictionaryKeyType { get; }

    /// <summary>
    /// Gets the dictionary value type when this property is dictionary-shaped.
    /// </summary>
    Type? DictionaryValueType { get; }

    /// <summary>
    /// Converts a JSON object member name into this dictionary property's CLR key type.
    /// </summary>
    /// <remarks>
    /// JSON object member names are always strings, while CLR dictionary keys may
    /// be enums, numbers, GUIDs, or other STJ-supported key types. Generated
    /// descriptors provide a closed generic delegate here so the binder does not
    /// need runtime generic construction on the normal Settings path.
    /// </remarks>
    Func<string, JsonSerializerOptions, object?>? DictionaryKeyReader { get; }

    /// <summary>
    /// Gets the unknown-member policy that applies to this property's object subtree.
    /// </summary>
    SettingsUnknownMemberHandling UnknownMemberHandling { get; }

    /// <summary>
    /// Gets the descriptor used to patch child properties, or <see langword="null"/> for terminal/scalar shapes.
    /// </summary>
    ISettingsDescriptor? ChildDescriptor { get; }

    /// <summary>
    /// Reads this property's current runtime value.
    /// </summary>
    object? GetValue(object instance);

    /// <summary>
    /// Replaces this property's runtime value.
    /// </summary>
    void SetValue(object instance, object? value);
}

/// <summary>
/// Builds settings descriptors by reflecting over the current settings model.
/// </summary>
/// <remarks>
/// This provider is the runtime bridge used before the source generator emits the
/// same descriptor shape. It follows System.Text.Json naming and ignore metadata.
/// </remarks>
public sealed class ReflectionSettingsDescriptorProvider : ISettingsDescriptorProvider
{
    private readonly ConcurrentDictionary<Type, Lazy<SettingsObjectDescriptor>> _cache = [];

    /// <summary>
    /// Gets or creates the descriptor for a CLR settings type.
    /// </summary>
    public ISettingsDescriptor GetDescriptor(Type type) =>
        _cache.GetOrAdd(type, t => new Lazy<SettingsObjectDescriptor>(() => CreateDescriptor(t))).Value;

    private SettingsObjectDescriptor CreateDescriptor(Type type)
    {
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .AsValueEnumerable()
            .Where(ShouldIncludeProperty)
            .Select(p => CreatePropertyDescriptor(type, p))
            .OfType<SettingsPropertyDescriptor>()
            .ToArray();

        return new ReflectionSettingsObjectDescriptor(
            type,
            properties,
            GetUnknownMemberHandling(type),
            HasSerializedSubtreeAttribute(type));
    }

    private SettingsPropertyDescriptor? CreatePropertyDescriptor(Type ownerType, PropertyInfo property)
    {
        var propertyType = property.PropertyType;
        var flags = GetPropertyAccessFlags(property);
        var nullability = new NullabilityInfoContext().Create(property);
        if (AllowsNull(propertyType, nullability.WriteState))
        {
            flags |= SettingsPropertyFlags.AllowsNull;
        }

        var jsonName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
        var unknownMemberHandling = GetUnknownMemberHandling(property, propertyType);
        var isArray = propertyType.IsArray;
        var isDictionary = TryGetDictionaryTypes(propertyType, out var dictionaryKeyType, out var dictionaryValueType);
        Type? elementType = null;
        var isList = !isArray && !isDictionary && TryGetListElementType(propertyType, out elementType);
        var kind =
            HasSerializedSubtreeAttribute(property) || HasSerializedSubtreeAttribute(propertyType) ? SettingsPropertyKind.SerializedSubtree :
            IsScalarType(propertyType) ? SettingsPropertyKind.Scalar :
            isArray ? SettingsPropertyKind.Array :
            isDictionary ? SettingsPropertyKind.Dictionary :
            isList ? SettingsPropertyKind.List :
            SettingsPropertyKind.Object;

        if (isArray)
        {
            elementType = propertyType.GetElementType();
        }
        else if (isDictionary)
        {
            elementType = dictionaryValueType;
        }

        if (elementType is not null && AllowsNull(
                elementType,
                GetElementNullability(nullability, elementType, isArray, isDictionary)))
        {
            flags |= SettingsPropertyFlags.ElementAllowsNull;
        }

        var canAssign = flags.HasFlag(SettingsPropertyFlags.CanWrite) ||
            flags.HasFlag(SettingsPropertyFlags.CanInitialize);
        ISettingsDescriptor? childDescriptor = null;
        if (kind == SettingsPropertyKind.Object)
        {
            if (CanPatchAsObject(propertyType))
            {
                childDescriptor = GetDescriptor(propertyType);
            }
            else if (!canAssign)
            {
                return null;
            }
        }

        if (!canAssign &&
            childDescriptor is null &&
            kind is not (SettingsPropertyKind.List or SettingsPropertyKind.Dictionary))
        {
            return null;
        }

        return new SettingsPropertyDescriptor(
            ownerType,
            property,
            jsonName,
            kind,
            flags,
            elementType,
            dictionaryKeyType,
            dictionaryValueType,
            dictionaryKeyType is null ? null : DictionaryKeyReader.Create(dictionaryKeyType),
            unknownMemberHandling,
            childDescriptor);
    }

    private static bool ShouldIncludeProperty(PropertyInfo property)
    {
        if (property.GetMethod is not { IsStatic: false }) return false;
        if (property.GetIndexParameters().Length != 0) return false;

        var jsonIgnore = property.GetCustomAttribute<JsonIgnoreAttribute>();
        return jsonIgnore is null || jsonIgnore.Condition == JsonIgnoreCondition.Never;
    }

    private static bool CanPatchAsObject(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsAbstract || type.IsInterface) return false;
        if (IsScalarType(type)) return false;
        if (typeof(Delegate).IsAssignableFrom(type)) return false;
        return true;
    }

    internal static bool IsScalarType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        return type.IsPrimitive ||
            type.IsValueType ||
            type.IsEnum ||
            type == typeof(string) ||
            type == typeof(decimal) ||
            type == typeof(Guid) ||
            type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) ||
            type == typeof(DateOnly) ||
            type == typeof(TimeOnly) ||
            type == typeof(TimeSpan) ||
            type == typeof(Uri) ||
            type == typeof(object) ||
            type.GetCustomAttribute<TypeConverterAttribute>() is not null;
    }

    internal static bool TryGetListElementType(Type type, out Type? elementType)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ObservableCollection<>))
        {
            elementType = type.GetGenericArguments()[0];
            return true;
        }

        var candidates = type.GetInterfaces().AsValueEnumerable().Append(type);

        var listType = candidates.FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>));
        if (listType is { IsGenericType: true })
        {
            elementType = listType.GetGenericArguments()[0];
            return true;
        }

        var readOnlyListType = candidates.FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyList<>));
        if (readOnlyListType is not null)
        {
            elementType = readOnlyListType.GetGenericArguments()[0];
            return true;
        }

        elementType = null;
        return false;
    }

    internal static bool TryGetDictionaryTypes(Type type, out Type? keyType, out Type? valueType)
    {
        var dictionaryType = type
            .GetInterfaces()
            .AsValueEnumerable()
            .Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        if (dictionaryType is not null)
        {
            var args = dictionaryType.GetGenericArguments();
            keyType = args[0];
            valueType = args[1];
            return true;
        }

        keyType = null;
        valueType = null;
        return false;
    }

    private static SettingsUnknownMemberHandling GetUnknownMemberHandling(MemberInfo member, Type? type = null)
    {
        while (true)
        {
            var settingsUnknownMemberHandling = member.GetCustomAttribute<SettingsUnknownMemberHandlingAttribute>()?.Handling;
            if (settingsUnknownMemberHandling is not null) return settingsUnknownMemberHandling.Value;
            if (type is null) return SettingsUnknownMemberHandling.Preserve;
            member = type;
            type = null;
        }
    }

    private static bool HasSerializedSubtreeAttribute(MemberInfo member) =>
        member.GetCustomAttribute<SettingsSerializedSubtreeAttribute>() is not null;

    private static SettingsPropertyFlags GetPropertyAccessFlags(PropertyInfo property)
    {
        if (property.SetMethod is not { IsStatic: false })
        {
            return SettingsPropertyFlags.None;
        }

        return IsInitOnly(property) ?
            SettingsPropertyFlags.CanInitialize :
            SettingsPropertyFlags.CanWrite;
    }

    private static bool IsInitOnly(PropertyInfo property) =>
        property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) is true;

    private static bool AllowsNull(Type type, NullabilityState nullability) =>
        Nullable.GetUnderlyingType(type) is not null ||
        (!type.IsValueType && nullability is not NullabilityState.NotNull);

    private static NullabilityState GetElementNullability(
        NullabilityInfo propertyNullability,
        Type elementType,
        bool isArray,
        bool isDictionary)
    {
        if (isArray)
        {
            return propertyNullability.ElementType?.WriteState ?? NullabilityState.Unknown;
        }

        NullabilityInfo? match = null;
        foreach (var argument in propertyNullability.GenericTypeArguments)
        {
            if (argument.Type != elementType)
            {
                continue;
            }

            match = argument;
            if (!isDictionary)
            {
                break;
            }
        }

        // Dictionary key and value types can be identical. Keeping the last
        // matching generic argument selects TValue for the common <TKey, TValue>
        // shape, while lists use their first matching element argument.
        return match?.WriteState ?? NullabilityState.Unknown;
    }
}

/// <summary>
/// Descriptor for a settings object type.
/// </summary>
public class SettingsObjectDescriptor : ISettingsDescriptor
{
    private readonly Dictionary<string, ISettingsPropertyDescriptor> _propertiesByName;

    /// <summary>
    /// Creates the shared descriptor base used by generated providers, including providers in plugin assemblies.
    /// </summary>
    protected internal SettingsObjectDescriptor(
        Type type,
        IReadOnlyList<ISettingsPropertyDescriptor> properties,
        SettingsUnknownMemberHandling unknownMemberHandling,
        bool isSerializedSubtree)
    {
        Type = type;
        Properties = properties;
        UnknownMemberHandling = unknownMemberHandling;
        IsSerializedSubtree = isSerializedSubtree;
        _propertiesByName = new Dictionary<string, ISettingsPropertyDescriptor>(StringComparer.Ordinal);

        foreach (var property in properties)
        {
            _propertiesByName.TryAdd(property.ClrName, property);
            _propertiesByName.TryAdd(property.JsonName, property);
        }
    }

    /// <inheritdoc />
    public Type Type { get; }

    /// <inheritdoc />
    public IReadOnlyList<ISettingsPropertyDescriptor> Properties { get; }

    /// <inheritdoc />
    public SettingsUnknownMemberHandling UnknownMemberHandling { get; }

    /// <inheritdoc />
    public bool IsSerializedSubtree { get; }

    /// <inheritdoc />
    public virtual bool TryCreateInstance(IReadOnlyDictionary<string, object?>? initialValues, out object? instance)
    {
        instance = null;
        return false;
    }

    /// <inheritdoc />
    public ISettingsPropertyDescriptor? FindProperty(string clrOrJsonName) => _propertiesByName.GetValueOrDefault(clrOrJsonName);
}

internal sealed class ReflectionSettingsObjectDescriptor : SettingsObjectDescriptor
{
    private readonly Type _instanceType;
    private readonly bool _canCreate;

    internal ReflectionSettingsObjectDescriptor(
        Type type,
        IReadOnlyList<ISettingsPropertyDescriptor> properties,
        SettingsUnknownMemberHandling unknownMemberHandling,
        bool isSerializedSubtree)
        : base(type, properties, unknownMemberHandling, isSerializedSubtree)
    {
        _instanceType = Nullable.GetUnderlyingType(type) ?? type;
        _canCreate = _instanceType.GetConstructor(Type.EmptyTypes) is not null;
    }

    public override bool TryCreateInstance(IReadOnlyDictionary<string, object?>? initialValues, out object? instance)
    {
        if (!_canCreate)
        {
            instance = null;
            return false;
        }

        try
        {
            instance = Activator.CreateInstance(_instanceType);
            if (instance is null)
            {
                return false;
            }

            foreach (var property in Properties)
            {
                if (!property.Flags.HasFlag(SettingsPropertyFlags.CanInitialize) ||
                    initialValues?.TryGetValue(property.JsonName, out var initialValue) is not true)
                {
                    continue;
                }

                property.SetValue(instance, initialValue);
            }

            return true;
        }
        catch
        {
            instance = null;
            return false;
        }
    }
}

/// <summary>
/// Delegate-backed descriptor for a generated settings property.
/// </summary>
/// <remarks>
/// The generated descriptor provider uses this class to avoid runtime
/// <see cref="PropertyInfo"/> lookup and reflection-based get/set calls for the
/// known settings graph. The property still exposes the same metadata shape as
/// <see cref="SettingsPropertyDescriptor"/> so the binder can stay oblivious to
/// where the descriptor came from.
/// </remarks>
public sealed class DelegateSettingsPropertyDescriptor : ISettingsPropertyDescriptor
{
    private readonly Func<object, object?> _getter;
    private readonly Action<object, object?>? _setter;

    /// <summary>
    /// Creates a generated property descriptor, including its caller-configured key conversion.
    /// </summary>
    public DelegateSettingsPropertyDescriptor(
        Type ownerType,
        string clrName,
        string jsonName,
        Type propertyType,
        SettingsPropertyKind kind,
        SettingsPropertyFlags flags,
        Type? elementType,
        Type? dictionaryKeyType,
        Type? dictionaryValueType,
        Func<string, JsonSerializerOptions, object?>? dictionaryKeyReader,
        SettingsUnknownMemberHandling unknownMemberHandling,
        ISettingsDescriptor? childDescriptor,
        Func<object, object?> getter,
        Action<object, object?>? setter)
    {
        OwnerType = ownerType;
        ClrName = clrName;
        JsonName = jsonName;
        PropertyType = propertyType;
        Kind = kind;
        Flags = flags;
        ElementType = elementType;
        DictionaryKeyType = dictionaryKeyType;
        DictionaryValueType = dictionaryValueType;
        DictionaryKeyReader = dictionaryKeyReader;
        UnknownMemberHandling = unknownMemberHandling;
        ChildDescriptor = childDescriptor;
        _getter = getter;
        _setter = setter;
    }

    /// <summary>
    /// Gets the type that owns the CLR property.
    /// </summary>
    public Type OwnerType { get; }

    /// <inheritdoc />
    public string ClrName { get; }

    /// <inheritdoc />
    public string JsonName { get; }

    /// <inheritdoc />
    public Type PropertyType { get; }

    /// <inheritdoc />
    public SettingsPropertyKind Kind { get; }

    /// <inheritdoc />
    public SettingsPropertyFlags Flags { get; }

    /// <inheritdoc />
    public Type? ElementType { get; }

    /// <inheritdoc />
    public Type? DictionaryKeyType { get; }

    /// <inheritdoc />
    public Type? DictionaryValueType { get; }

    /// <inheritdoc />
    public Func<string, JsonSerializerOptions, object?>? DictionaryKeyReader { get; }

    /// <inheritdoc />
    public SettingsUnknownMemberHandling UnknownMemberHandling { get; }

    /// <inheritdoc />
    public ISettingsDescriptor? ChildDescriptor { get; }

    /// <inheritdoc />
    public object? GetValue(object instance) => _getter(instance);

    /// <inheritdoc />
    public void SetValue(object instance, object? value)
    {
        if (_setter is null)
        {
            throw new InvalidOperationException($"Settings property '{OwnerType.Name}.{ClrName}' is not writable.");
        }

        _setter(instance, value);
    }
}

/// <summary>
/// Reflection-backed descriptor for a single settings property.
/// </summary>
public sealed class SettingsPropertyDescriptor : ISettingsPropertyDescriptor
{
    private readonly PropertyInfo _property;

    internal SettingsPropertyDescriptor(
        Type ownerType,
        PropertyInfo property,
        string jsonName,
        SettingsPropertyKind kind,
        SettingsPropertyFlags flags,
        Type? elementType,
        Type? dictionaryKeyType,
        Type? dictionaryValueType,
        Func<string, JsonSerializerOptions, object?>? dictionaryKeyReader,
        SettingsUnknownMemberHandling unknownMemberHandling,
        ISettingsDescriptor? childDescriptor)
    {
        OwnerType = ownerType;
        _property = property;
        JsonName = jsonName;
        Kind = kind;
        Flags = flags;
        ElementType = elementType;
        DictionaryKeyType = dictionaryKeyType;
        DictionaryValueType = dictionaryValueType;
        DictionaryKeyReader = dictionaryKeyReader;
        UnknownMemberHandling = unknownMemberHandling;
        ChildDescriptor = childDescriptor;
    }

    /// <summary>
    /// Gets the type that owns the CLR property.
    /// </summary>
    public Type OwnerType { get; }

    /// <inheritdoc />
    public string ClrName => _property.Name;

    /// <inheritdoc />
    public string JsonName { get; }

    /// <inheritdoc />
    public Type PropertyType => _property.PropertyType;

    /// <inheritdoc />
    public SettingsPropertyKind Kind { get; }

    /// <inheritdoc />
    public SettingsPropertyFlags Flags { get; }

    /// <inheritdoc />
    public Type? ElementType { get; }

    /// <inheritdoc />
    public Type? DictionaryKeyType { get; }

    /// <inheritdoc />
    public Type? DictionaryValueType { get; }

    /// <inheritdoc />
    public Func<string, JsonSerializerOptions, object?>? DictionaryKeyReader { get; }

    /// <inheritdoc />
    public SettingsUnknownMemberHandling UnknownMemberHandling { get; }

    /// <inheritdoc />
    public ISettingsDescriptor? ChildDescriptor { get; }

    /// <inheritdoc />
    public object? GetValue(object instance) => _property.GetValue(instance);

    /// <inheritdoc />
    public void SetValue(object instance, object? value) => _property.SetValue(instance, value);
}

/// <summary>
/// Creates the default descriptor provider for SettingsEngine.
/// </summary>
/// <remarks>
/// The source generator implements <see cref="TryCreateGenerated"/> inside the
/// application assembly. When generation is not available, the reflection
/// provider remains the compatibility fallback.
/// </remarks>
internal static partial class SettingsDescriptorProviderFactory
{
    /// <summary>
    /// Creates the best available descriptor provider.
    /// </summary>
    public static ISettingsDescriptorProvider Create()
    {
        ISettingsDescriptorProvider? provider = null;
        TryCreateGenerated(ref provider);
        return provider ?? new ReflectionSettingsDescriptorProvider();
    }

    static partial void TryCreateGenerated(ref ISettingsDescriptorProvider? provider);
}
