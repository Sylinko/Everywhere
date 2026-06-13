using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Everywhere.StrategyEngine.ConditionExpression.PathBinding;

namespace Everywhere.StrategyEngine.ConditionExpression;

/// <summary>
/// Builds Condition DSL path accessors from System.Text.Json metadata.
/// </summary>
/// <remarks>
/// This is the schema adapter for M6+: <c>JsonPropertyName</c> is the public DSL name, <c>JsonIgnore</c> removes
/// members from the DSL, and generated <c>JsonTypeInfo</c> accessors are preferred over reflection when available.
/// </remarks>
internal sealed class JsonStrategyPathAccessorProvider : IStrategyPathAccessorProvider
{
    private readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, StrategyPathAccessor>> _cache = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = ConditionExpressionJsonSerializerContext.Default
    };

    /// <summary>
    /// Resolves an exact public JSON/DSL property name for a CLR type.
    /// </summary>
    /// <remarks>
    /// Matching is intentionally case-sensitive so the DSL surface remains stable and mirrors serialized JSON.
    /// Diagnostics can still suggest near matches before binding fails.
    /// </remarks>
    public bool TryGetAccessor(Type type, string publicName, out StrategyPathAccessor accessor) =>
        GetAccessors(type).TryGetValue(publicName, out accessor!);

    /// <summary>
    /// Returns all bindable public names for suggestion diagnostics.
    /// </summary>
    public IReadOnlyList<string> GetKnownNames(Type type) => GetAccessors(type).Keys.ToArray();

    private IReadOnlyDictionary<string, StrategyPathAccessor> GetAccessors(Type type) =>
        _cache.GetOrAdd(type, CreateAccessors);

    private IReadOnlyDictionary<string, StrategyPathAccessor> CreateAccessors(Type type)
    {
        JsonTypeInfo? typeInfo = null;
        try
        {
            typeInfo = _jsonOptions.GetTypeInfo(type);
        }
        catch (NotSupportedException)
        {
            // Types outside the generated DSL context intentionally fall back to reflection.
        }

        if (typeInfo?.Kind == JsonTypeInfoKind.Object)
        {
            var accessors = new Dictionary<string, StrategyPathAccessor>(StringComparer.Ordinal);
            foreach (var property in typeInfo.Properties)
            {
                if (property.Get is null || ConditionExpressionKeywords.IsReserved(property.Name))
                {
                    continue;
                }

                accessors[property.Name] = new StrategyPathAccessor(
                    property.Name,
                    type,
                    property.PropertyType,
                    instance => property.Get(instance));
            }

            return accessors;
        }

        return CreateReflectionAccessors(type);
    }

    private static IReadOnlyDictionary<string, StrategyPathAccessor> CreateReflectionAccessors(Type type)
    {
        var accessors = new Dictionary<string, StrategyPathAccessor>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetMethod is null ||
                property.GetCustomAttribute<JsonIgnoreAttribute>() is not null ||
                property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var publicName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? ToCamelCase(property.Name);
            if (ConditionExpressionKeywords.IsReserved(publicName))
            {
                continue;
            }

            accessors[publicName] = new StrategyPathAccessor(
                publicName,
                type,
                property.PropertyType,
                instance => property.GetValue(instance));
        }

        return accessors;
    }

    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value) || char.IsLower(value[0]))
        {
            return value;
        }

        return char.ToLowerInvariant(value[0]) + value[1..];
    }
}
