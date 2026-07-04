using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
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
    bool TryCreateInstance(IServiceProvider serviceProvider, out object? instance);

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
    Func<string, object?>? DictionaryKeyReader { get; }

    /// <summary>
    /// Gets whether the property can be replaced through its setter.
    /// </summary>
    bool CanWrite { get; }

    /// <summary>
    /// Gets the mutually exclusive binding shape for this property.
    /// </summary>
    SettingsPropertyKind Kind { get; }

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
            .ToList();

        return new SettingsObjectDescriptor(
            type,
            properties,
            GetUnknownMemberHandling(type),
            HasSerializedSubtreeAttribute(type),
            CreateInstanceFactory(type));
    }

    private SettingsPropertyDescriptor? CreatePropertyDescriptor(Type ownerType, PropertyInfo property)
    {
        var propertyType = property.PropertyType;
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

        ISettingsDescriptor? childDescriptor = null;
        if (kind == SettingsPropertyKind.Object)
        {
            if (CanPatchAsObject(propertyType))
            {
                childDescriptor = GetDescriptor(propertyType);
            }
            else if (!property.CanWrite)
            {
                return null;
            }
        }

        if (!property.CanWrite &&
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

        var readOnlyListType = candidates.FirstOrDefault(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyList<>));
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

    private static Func<IServiceProvider, object?>? CreateInstanceFactory(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (!constructors.Any(static constructor =>
                constructor.GetParameters() is var parameters &&
                (parameters.Length == 0 ||
                    parameters.Length == 1 && parameters[0].ParameterType == typeof(IServiceProvider))))
        {
            return null;
        }

        return serviceProvider => ActivatorUtilities.CreateInstance(serviceProvider, type);
    }
}

/// <summary>
/// Descriptor for a settings object type.
/// </summary>
public sealed class SettingsObjectDescriptor : ISettingsDescriptor
{
    private readonly Dictionary<string, ISettingsPropertyDescriptor> _propertiesByName;
    private readonly Func<IServiceProvider, object?>? _instanceFactory;

    internal SettingsObjectDescriptor(
        Type type,
        IReadOnlyList<ISettingsPropertyDescriptor> properties,
        SettingsUnknownMemberHandling unknownMemberHandling,
        bool isSerializedSubtree,
        Func<IServiceProvider, object?>? instanceFactory)
    {
        Type = type;
        Properties = properties;
        UnknownMemberHandling = unknownMemberHandling;
        IsSerializedSubtree = isSerializedSubtree;
        _instanceFactory = instanceFactory;
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
    public bool TryCreateInstance(IServiceProvider serviceProvider, out object? instance)
    {
        if (_instanceFactory is null)
        {
            instance = null;
            return false;
        }

        try
        {
            instance = _instanceFactory(serviceProvider);
            return instance is not null;
        }
        catch
        {
            instance = null;
            return false;
        }
    }

    /// <inheritdoc />
    public ISettingsPropertyDescriptor? FindProperty(string clrOrJsonName) =>
        _propertiesByName.GetValueOrDefault(clrOrJsonName);
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
internal sealed class DelegateSettingsPropertyDescriptor : ISettingsPropertyDescriptor
{
    private readonly Func<object, object?> _getter;
    private readonly Action<object, object?>? _setter;

    internal DelegateSettingsPropertyDescriptor(
        Type ownerType,
        string clrName,
        string jsonName,
        Type propertyType,
        bool canWrite,
        SettingsPropertyKind kind,
        Type? elementType,
        Type? dictionaryKeyType,
        Type? dictionaryValueType,
        Func<string, object?>? dictionaryKeyReader,
        SettingsUnknownMemberHandling unknownMemberHandling,
        ISettingsDescriptor? childDescriptor,
        Func<object, object?> getter,
        Action<object, object?>? setter)
    {
        OwnerType = ownerType;
        ClrName = clrName;
        JsonName = jsonName;
        PropertyType = propertyType;
        CanWrite = canWrite;
        Kind = kind;
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
    public Type? ElementType { get; }

    /// <inheritdoc />
    public Type? DictionaryKeyType { get; }

    /// <inheritdoc />
    public Type? DictionaryValueType { get; }

    /// <inheritdoc />
    public Func<string, object?>? DictionaryKeyReader { get; }

    /// <inheritdoc />
    public bool CanWrite { get; }

    /// <inheritdoc />
    public SettingsPropertyKind Kind { get; }

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
        Type? elementType,
        Type? dictionaryKeyType,
        Type? dictionaryValueType,
        Func<string, object?>? dictionaryKeyReader,
        SettingsUnknownMemberHandling unknownMemberHandling,
        ISettingsDescriptor? childDescriptor)
    {
        OwnerType = ownerType;
        _property = property;
        JsonName = jsonName;
        Kind = kind;
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
    public Type? ElementType { get; }

    /// <inheritdoc />
    public Type? DictionaryKeyType { get; }

    /// <inheritdoc />
    public Type? DictionaryValueType { get; }

    /// <inheritdoc />
    public Func<string, object?>? DictionaryKeyReader { get; }

    /// <inheritdoc />
    public bool CanWrite => _property is { CanWrite: true, SetMethod.IsStatic: false };

    /// <inheritdoc />
    public SettingsPropertyKind Kind { get; }

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
