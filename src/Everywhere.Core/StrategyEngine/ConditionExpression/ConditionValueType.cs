namespace Everywhere.StrategyEngine.ConditionExpression;

/// <summary>
/// Coarse static categories used by binding and operator validation.
/// </summary>
/// <remarks>
/// These categories describe Condition DSL semantics rather than JSON token kinds. For example, arrays and
/// <c>IEnumerable&lt;T&gt;</c> values are both <see cref="ConditionValueKind.Collection"/>, and deferred roots bind as
/// <see cref="ConditionValueKind.Unknown"/> until runtime.
/// </remarks>
internal enum ConditionValueKind
{
    Null,
    Bool,
    String,
    Number,
    Object,
    Collection,
    Unknown
}

/// <summary>
/// Static type descriptor carried by bound paths and operators.
/// </summary>
/// <remarks>
/// The binder uses this to reject impossible expressions early, such as <c>startsWith</c> on
/// <c>attachments.files</c>. Runtime still performs defensive checks because deferred roots and provider data can
/// have less precise shapes.
/// </remarks>
internal sealed record ConditionValueType(ConditionValueKind Kind, Type ClrType, ConditionValueType? ElementType = null)
{
    /// <summary>
    /// Represents a literal null operand.
    /// </summary>
    public static ConditionValueType Null { get; } = new(ConditionValueKind.Null, typeof(object));

    /// <summary>
    /// Represents a value that is allowed to bind now and be checked at runtime.
    /// </summary>
    public static ConditionValueType Unknown { get; } = new(ConditionValueKind.Unknown, typeof(object));

    public bool IsScalar => Kind is ConditionValueKind.Bool or ConditionValueKind.String or ConditionValueKind.Number;

    /// <summary>
    /// Projects a CLR type into the small type lattice understood by the DSL.
    /// </summary>
    public static ConditionValueType FromClrType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return new ConditionValueType(ConditionValueKind.String, type);
        if (type == typeof(bool)) return new ConditionValueType(ConditionValueKind.Bool, type);
        if (IsNumberType(type)) return new ConditionValueType(ConditionValueKind.Number, type);
        if (TryGetCollectionElementType(type, out var elementType))
        {
            return new ConditionValueType(ConditionValueKind.Collection, type, FromClrType(elementType));
        }

        return new ConditionValueType(ConditionValueKind.Object, type);
    }

    public override string ToString() =>
        Kind == ConditionValueKind.Collection ? $"collection<{ElementType}>" : Kind.ToString().ToLowerInvariant();

    private static bool IsNumberType(Type type) =>
        type == typeof(decimal) ||
        type.IsPrimitive &&
        (type == typeof(byte) ||
            type == typeof(sbyte) ||
            type == typeof(short) ||
            type == typeof(ushort) ||
            type == typeof(int) ||
            type == typeof(uint) ||
            type == typeof(long) ||
            type == typeof(ulong) ||
            type == typeof(float) ||
            type == typeof(double));

    private static bool TryGetCollectionElementType(Type type, out Type elementType)
    {
        if (type == typeof(string))
        {
            elementType = typeof(char);
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType() ?? typeof(object);
            return true;
        }

        var enumerableType = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>) ?
            type :
            type.GetInterfaces().AsValueEnumerable().FirstOrDefault(candidate =>
                candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerableType is null)
        {
            elementType = typeof(object);
            return false;
        }

        elementType = enumerableType.GetGenericArguments()[0];
        return true;
    }
}