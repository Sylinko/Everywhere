using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Everywhere.Configuration.Engine;

/// <summary>
/// Applies a settings JSON document to an existing runtime settings object and writes observed runtime changes back to JSON.
/// </summary>
/// <remarks>
/// The binder borrows the object-patching shape of <c>IConfiguration.Binder</c>
/// (reuse existing objects where possible), but it keeps values as JSON nodes
/// instead of flattening them to strings.
/// </remarks>
public sealed class SettingsPatchBinder
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsDescriptorProvider _descriptorProvider;
    private readonly List<SettingsEngineDiagnostic> _diagnostics = [];

    /// <summary>
    /// Creates a binder with the generated descriptor provider when available.
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <param name="descriptorProvider">
    /// Optional provider used by tests or specialized hosts. When omitted, the
    /// source-generated provider is preferred and reflection is used as a
    /// compatibility fallback.
    /// </param>
    public SettingsPatchBinder(IServiceProvider serviceProvider, ISettingsDescriptorProvider? descriptorProvider = null)
    {
        _serviceProvider = serviceProvider;
        _descriptorProvider = descriptorProvider ?? SettingsDescriptorProviderFactory.Create();
    }

    /// <summary>
    /// Gets binding diagnostics collected during reads and write-back.
    /// </summary>
    public IReadOnlyList<SettingsEngineDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Gets the descriptor for a settings-reachable type.
    /// </summary>
    public ISettingsDescriptor GetDescriptor(Type type) => _descriptorProvider.GetDescriptor(type);

    /// <summary>
    /// Patches an existing runtime object from a JSON object.
    /// </summary>
    public void Patch(JsonObject root, object target)
    {
        var descriptor = _descriptorProvider.GetDescriptor(target.GetType());
        PatchObject(root, descriptor, target, string.Empty, descriptor.UnknownMemberHandling);
    }

    /// <summary>
    /// Writes an observed CLR object path back to the settings JSON document.
    /// </summary>
    /// <remarks>
    /// <paramref name="observedPath"/> uses <see cref="Everywhere.Utilities.ObjectObserver"/>'s
    /// colon-separated CLR path. This method resolves it through descriptors into
    /// a typed JSON path so numeric dictionary keys stay object properties while
    /// collection indexes become array segments.
    /// </remarks>
    public void WriteObservedPath(JsonSettingsStorage store, ISettingsDescriptor rootDescriptor, string observedPath, object? value)
    {
        try
        {
            var resolution = ResolveObservedPath(rootDescriptor, observedPath);
            var valueType = resolution.DeclaredType ?? value?.GetType() ?? typeof(object);
            var node = SettingsEngineJson.SerializeToNode(value, valueType);
            store.ReplaceSubtree(resolution.JsonPath, node);
        }
        catch (Exception ex)
        {
            var diagnostic = new SettingsEngineDiagnostic(
                SettingsEngineDiagnosticKind.WriteFailure,
                SettingsEngineDiagnosticSeverity.Error,
                observedPath,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_WriteObservedPathFailed, observedPath),
                ex);

            _diagnostics.Add(diagnostic);
            store.AddDiagnostic(diagnostic);
        }
    }

    private void PatchObject(
        JsonObject json,
        ISettingsDescriptor descriptor,
        object target,
        string path,
        SettingsUnknownMemberHandling unknownMemberHandling)
    {
        var knownNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in descriptor.Properties)
        {
            knownNames.Add(property.JsonName);
            if (!json.TryGetPropertyValue(property.JsonName, out var node))
            {
                continue;
            }

            var propertyPath = CombinePath(path, property.JsonName);
            PatchProperty(node, property, target, propertyPath);
        }

        HandleUnknownMembers(json, knownNames, path, unknownMemberHandling);
    }

    private void PatchProperty(JsonNode? node, ISettingsPropertyDescriptor property, object target, string path)
    {
        switch (property.Kind)
        {
            case SettingsPropertyKind.SerializedSubtree:
                PatchSerializedSubtree(node, property, target, path);
                break;
            case SettingsPropertyKind.Array:
                PatchArray(node, property, target, path);
                break;
            case SettingsPropertyKind.Dictionary:
                PatchDictionary(node, property, target, path);
                break;
            case SettingsPropertyKind.List:
                PatchList(node, property, target, path);
                break;
            case SettingsPropertyKind.Scalar:
                PatchScalar(node, property, target, path);
                break;
            case SettingsPropertyKind.Object:
                PatchComplexObject(node, property, target, path);
                break;
            default:
                AddDiagnostic(
                    SettingsEngineDiagnosticKind.UnsupportedShape,
                    SettingsEngineDiagnosticSeverity.Warning,
                    path,
                    DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_UnsupportedPropertyKind, property.Kind, path));
                break;
        }
    }

    private void PatchSerializedSubtree(JsonNode? node, ISettingsPropertyDescriptor property, object target, string path)
    {
        if (!property.CanWrite)
        {
            AddDiagnostic(
                SettingsEngineDiagnosticKind.SerializedSubtreeFailure,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_SerializedSubtreeNotWritable, path));
            return;
        }

        if (!TryDeserialize(node, property.PropertyType, path, SettingsEngineDiagnosticKind.SerializedSubtreeFailure, out var value))
        {
            return;
        }

        property.SetValue(target, value);
    }

    private void PatchScalar(JsonNode? node, ISettingsPropertyDescriptor property, object target, string path)
    {
        if (!property.CanWrite)
        {
            return;
        }

        if (!TryDeserialize(node, property.PropertyType, path, SettingsEngineDiagnosticKind.ScalarConversionFailure, out var value))
        {
            return;
        }

        property.SetValue(target, value);
    }

    private void PatchArray(JsonNode? node, ISettingsPropertyDescriptor property, object target, string path)
    {
        if (!property.CanWrite)
        {
            return;
        }

        if (!TryDeserialize(node, property.PropertyType, path, SettingsEngineDiagnosticKind.ScalarConversionFailure, out var value))
        {
            return;
        }

        property.SetValue(target, value);
    }

    private void PatchList(JsonNode? node, ISettingsPropertyDescriptor property, object target, string path)
    {
        if (node is not JsonArray array)
        {
            AddDiagnostic(
                SettingsEngineDiagnosticKind.ScalarConversionFailure,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_ExpectedJsonArrayForList, path));
            return;
        }

        var currentValue = property.GetValue(target);
        if (currentValue is IList { IsFixedSize: false, IsReadOnly: false } list)
        {
            var elementType = property.ElementType ?? typeof(object);
            var commonCount = Math.Min(list.Count, array.Count);

            // Keep the collection object stable for UI bindings and observers.
            // Existing object items are patched in place by index; terminal values
            // are replaced at the same index only after successful conversion.
            for (var i = 0; i < commonCount; i++)
            {
                var entryPath = CombinePath(path, i.ToString(CultureInfo.InvariantCulture));
                var result = PatchValue(
                    array[i],
                    elementType,
                    list[i],
                    entryPath,
                    SettingsEngineDiagnosticKind.ScalarConversionFailure,
                    out var itemReplacement);

                if (result == ValuePatchResult.Replace)
                {
                    list[i] = itemReplacement;
                }
            }

            for (var i = commonCount; i < array.Count; i++)
            {
                if (TryCreateValue(
                        array[i],
                        elementType,
                        CombinePath(path, i.ToString(CultureInfo.InvariantCulture)),
                        out var item))
                {
                    list.Add(item);
                }
            }

            for (var i = list.Count - 1; i >= array.Count; i--)
            {
                list.RemoveAt(i);
            }

            return;
        }

        if (!property.CanWrite)
        {
            AddDiagnostic(
                SettingsEngineDiagnosticKind.UnsupportedShape,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_ListNotMutable, path));
            return;
        }

        if (TryDeserialize(node, property.PropertyType, path, SettingsEngineDiagnosticKind.ScalarConversionFailure, out var replacement))
        {
            property.SetValue(target, replacement);
        }
    }

    private void PatchDictionary(JsonNode? node, ISettingsPropertyDescriptor property, object target, string path)
    {
        if (node is not JsonObject obj)
        {
            AddDiagnostic(
                SettingsEngineDiagnosticKind.ScalarConversionFailure,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_ExpectedJsonObjectForDictionary, path));
            return;
        }

        var currentValue = property.GetValue(target);
        if (currentValue is IDictionary { IsFixedSize: false, IsReadOnly: false } dictionary)
        {
            var valueType = property.DictionaryValueType ?? typeof(object);
            var seenKeys = new HashSet<object>();

            // Mutable dictionaries keep their identity. Matching object values
            // are patched in place, while scalar values and serialized subtrees
            // are replaced only after the JSON value converts successfully.
            foreach (var pair in obj)
            {
                var entryPath = CombinePath(path, pair.Key);
                if (!TryConvertDictionaryKey(pair.Key, property, entryPath, out var key))
                {
                    continue;
                }

                seenKeys.Add(key);

                if (dictionary.Contains(key))
                {
                    var result = PatchValue(
                        pair.Value,
                        valueType,
                        dictionary[key],
                        entryPath,
                        SettingsEngineDiagnosticKind.ScalarConversionFailure,
                        out var valueReplacement);

                    if (result == ValuePatchResult.Replace)
                    {
                        dictionary[key] = valueReplacement;
                    }

                    continue;
                }

                if (TryCreateValue(pair.Value, valueType, entryPath, out var value))
                {
                    dictionary[key] = value;
                }
            }

            foreach (var key in dictionary.Keys.Cast<object>().Where(key => !seenKeys.Contains(key)).ToArray())
            {
                dictionary.Remove(key);
            }

            return;
        }

        if (currentValue is IDictionary { IsReadOnly: true } readOnlyDictionary &&
            TryPatchReadOnlyDictionaryEntries(obj, readOnlyDictionary, property, path))
        {
            return;
        }

        if (!property.CanWrite)
        {
            AddDiagnostic(
                SettingsEngineDiagnosticKind.UnsupportedShape,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_DictionaryNotMutable, path));
            return;
        }

        if (TryDeserialize(node, property.PropertyType, path, SettingsEngineDiagnosticKind.ScalarConversionFailure, out var replacement))
        {
            property.SetValue(target, replacement);
        }
    }

    private bool TryPatchReadOnlyDictionaryEntries(
        JsonObject obj,
        IDictionary dictionary,
        ISettingsPropertyDescriptor property,
        string path)
    {
        var patchedAny = false;
        var isObjectValueDictionary = property.DictionaryValueType is { } valueType &&
            !ReflectionSettingsDescriptorProvider.IsScalarType(valueType);

        foreach (var pair in obj)
        {
            var entryPath = CombinePath(path, pair.Key);
            if (!TryConvertDictionaryKey(pair.Key, property, entryPath, out var key))
            {
                continue;
            }

            if (!dictionary.Contains(key))
            {
                continue;
            }

            var existingValue = dictionary[key];
            if (existingValue is null || pair.Value is not JsonObject childObj)
            {
                continue;
            }

            patchedAny |= TryPatchObjectInPlace(childObj, existingValue, entryPath);
        }

        // Immutable dictionaries such as WebSearchEngineSettings.Providers keep
        // stable object instances as values. Unknown keys are preserved in JSON,
        // while matching entries are patched in place above.
        return patchedAny || isObjectValueDictionary;
    }

    private ValuePatchResult PatchValue(
        JsonNode? node,
        Type declaredType,
        object? existingValue,
        string path,
        SettingsEngineDiagnosticKind failureKind,
        out object? replacement)
    {
        replacement = null;

        if (existingValue is not null &&
            node is JsonObject obj &&
            TryPatchObjectInPlace(obj, existingValue, path))
        {
            return ValuePatchResult.PatchedInPlace;
        }

        return TryCreateValue(node, declaredType, path, failureKind, out replacement) ?
            ValuePatchResult.Replace :
            ValuePatchResult.Failed;
    }

    private bool TryCreateValue(JsonNode? node, Type declaredType, string path, out object? value) =>
        TryCreateValue(node, declaredType, path, SettingsEngineDiagnosticKind.ScalarConversionFailure, out value);

    private bool TryCreateValue(
        JsonNode? node,
        Type declaredType,
        string path,
        SettingsEngineDiagnosticKind failureKind,
        out object? value)
    {
        value = null;
        var normalizedType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        if (node is JsonObject obj &&
            TryGetPatchDescriptor(normalizedType, out var descriptor) &&
            descriptor.TryCreateInstance(_serviceProvider, out value) &&
            value is not null)
        {
            PatchObject(obj, descriptor, value, path, descriptor.UnknownMemberHandling);
            return true;
        }

        return TryDeserialize(node, declaredType, path, failureKind, out value);
    }

    private bool TryPatchObjectInPlace(JsonObject obj, object target, string path)
    {
        if (!TryGetPatchDescriptor(target.GetType(), out var descriptor))
        {
            return false;
        }

        PatchObject(obj, descriptor, target, path, descriptor.UnknownMemberHandling);
        return true;
    }

    private bool TryGetPatchDescriptor(Type type, [NotNullWhen(true)] out ISettingsDescriptor? descriptor)
    {
        descriptor = null;
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type.IsAbstract || type.IsInterface) return false;
        if (ReflectionSettingsDescriptorProvider.IsScalarType(type)) return false;
        if (ReflectionSettingsDescriptorProvider.TryGetListElementType(type, out _)) return false;
        if (ReflectionSettingsDescriptorProvider.TryGetDictionaryTypes(type, out _, out _)) return false;

        descriptor = _descriptorProvider.GetDescriptor(type);
        if (descriptor.IsSerializedSubtree)
        {
            descriptor = null;
            return false;
        }

        return true;
    }

    private void PatchComplexObject(JsonNode? node, ISettingsPropertyDescriptor property, object target, string path)
    {
        if (node is not JsonObject obj)
        {
            AddDiagnostic(
                SettingsEngineDiagnosticKind.ScalarConversionFailure,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_ExpectedJsonObjectForObject, path));
            return;
        }

        var value = property.GetValue(target);
        if (value is null)
        {
            if (property.ChildDescriptor is not { } childDescriptor ||
                !property.CanWrite ||
                !childDescriptor.TryCreateInstance(_serviceProvider, out value))
            {
                AddDiagnostic(
                    SettingsEngineDiagnosticKind.UnsupportedShape,
                    SettingsEngineDiagnosticSeverity.Warning,
                    path,
                    DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_ObjectCannotBeCreated, path));
                return;
            }

            property.SetValue(target, value);
        }

        if (property.ChildDescriptor is { } descriptor && value is not null)
        {
            PatchObject(obj, descriptor, value, path, property.UnknownMemberHandling);
        }
    }

    private void HandleUnknownMembers(
        JsonObject json,
        HashSet<string> knownNames,
        string path,
        SettingsUnknownMemberHandling unknownMemberHandling)
    {
        if (unknownMemberHandling == SettingsUnknownMemberHandling.Preserve)
        {
            return;
        }

        foreach (var unknownName in json.Select(p => p.Key).Where(k => !knownNames.Contains(k)).ToArray())
        {
            var unknownPath = CombinePath(path, unknownName);
            if (unknownMemberHandling == SettingsUnknownMemberHandling.Prune)
            {
                json.Remove(unknownName);
                continue;
            }

            AddDiagnostic(
                SettingsEngineDiagnosticKind.UnknownMember,
                SettingsEngineDiagnosticSeverity.Warning,
                unknownPath,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_UnknownMember, unknownPath));
        }
    }

    private bool TryDeserialize(
        JsonNode? node,
        Type type,
        string path,
        SettingsEngineDiagnosticKind failureKind,
        out object? value)
    {
        value = null;

        if (node is null)
        {
            if (IsNullable(type))
            {
                return true;
            }

            AddDiagnostic(
                failureKind,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_NullForNonNullable, path));
            return false;
        }

        try
        {
            value = SettingsEngineJson.Deserialize(node, type);
            return true;
        }
        catch (Exception ex)
        {
            AddDiagnostic(
                failureKind,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_ReadValueFailed, path, type.Name),
                ex);
            return false;
        }
    }

    private bool TryConvertDictionaryKey(
        string keyText,
        ISettingsPropertyDescriptor property,
        string path,
        [NotNullWhen(true)] out object? key)
    {
        var keyType = property.DictionaryKeyType ?? typeof(string);

        try
        {
            if (property.DictionaryKeyReader is not { } reader)
            {
                throw new InvalidOperationException($"Settings property '{property.ClrName}' does not have a dictionary key reader.");
            }

            key = reader(keyText);
            if (key is not null) return true;

            AddDiagnostic(
                SettingsEngineDiagnosticKind.ScalarConversionFailure,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_DictionaryKeyFailed, path, keyType.Name));
            return false;
        }
        catch (Exception ex)
        {
            key = null;
            AddDiagnostic(
                SettingsEngineDiagnosticKind.ScalarConversionFailure,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_DictionaryKeyFailed, path, keyType.Name),
                ex);
            return false;
        }
    }

    private PathResolution ResolveObservedPath(ISettingsDescriptor rootDescriptor, string observedPath)
    {
        var jsonSegments = new List<SettingsJsonPathSegment>();
        var currentDescriptor = rootDescriptor;
        ISettingsDescriptor? indexedValueDescriptor = null;
        Type? indexedValueType = null;

        var declaredType = rootDescriptor.Type;
        var expectingListIndex = false;
        var expectingDictionaryKey = false;

        foreach (var segment in SplitObservedPath(observedPath))
        {
            if (expectingListIndex)
            {
                if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    throw new InvalidOperationException($"Observed settings path segment '{segment}' must be an array index.");
                }

                jsonSegments.Add(SettingsJsonPathSegment.Index(index));
                declaredType = indexedValueType ?? declaredType;
                currentDescriptor = indexedValueDescriptor ?? currentDescriptor;
                expectingListIndex = false;
                continue;
            }

            if (expectingDictionaryKey)
            {
                jsonSegments.Add(SettingsJsonPathSegment.Property(segment));
                declaredType = indexedValueType ?? declaredType;
                currentDescriptor = indexedValueDescriptor ?? currentDescriptor;
                expectingDictionaryKey = false;
                continue;
            }

            if (currentDescriptor.FindProperty(segment) is { } property)
            {
                jsonSegments.Add(SettingsJsonPathSegment.Property(property.JsonName));
                declaredType = property.PropertyType;

                if (property.Kind is SettingsPropertyKind.Array or SettingsPropertyKind.List)
                {
                    indexedValueType = property.ElementType;
                    indexedValueDescriptor = GetChildDescriptor(indexedValueType);
                    expectingListIndex = true;
                }
                else if (property.Kind == SettingsPropertyKind.Dictionary)
                {
                    indexedValueType = property.DictionaryValueType;
                    indexedValueDescriptor = GetChildDescriptor(indexedValueType);
                    expectingDictionaryKey = true;
                }
                else
                {
                    indexedValueType = null;
                    indexedValueDescriptor = null;
                    currentDescriptor = property.ChildDescriptor ?? currentDescriptor;
                }

                continue;
            }

            jsonSegments.Add(SettingsJsonPathSegment.Property(segment));
            declaredType = indexedValueType ?? declaredType;
            currentDescriptor = indexedValueDescriptor ?? currentDescriptor;
        }

        return new PathResolution(new SettingsJsonPath(jsonSegments), declaredType);
    }

    private ISettingsDescriptor? GetChildDescriptor(Type? type)
    {
        if (type is null) return null;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (ReflectionSettingsDescriptorProvider.IsScalarType(type)) return null;
        if (type.IsAbstract || type.IsInterface) return null;
        if (ReflectionSettingsDescriptorProvider.TryGetListElementType(type, out _)) return null;
        if (ReflectionSettingsDescriptorProvider.TryGetDictionaryTypes(type, out _, out _)) return null;
        return _descriptorProvider.GetDescriptor(type);
    }

    private void AddDiagnostic(
        SettingsEngineDiagnosticKind kind,
        SettingsEngineDiagnosticSeverity severity,
        string path,
        IDynamicLocaleKey message,
        Exception? exception = null) =>
        _diagnostics.Add(new SettingsEngineDiagnostic(kind, severity, path, message, exception));

    private static IDynamicLocaleKey DiagnosticMessage(string key, params object?[] args) =>
        args.Length == 0 ?
            new DynamicLocaleKey(key) :
            new FormattedDynamicLocaleKey(
                key,
                args.Select(static arg => new DirectLocaleKey(arg)).ToArray());

    private static bool IsNullable(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static string CombinePath(string prefix, string segment) =>
        string.IsNullOrEmpty(prefix) ? segment : $"{prefix}.{segment}";

    private static string[] SplitObservedPath(string observedPath) =>
        observedPath.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private enum ValuePatchResult
    {
        Failed,
        PatchedInPlace,
        Replace
    }

    private readonly record struct PathResolution(SettingsJsonPath JsonPath, Type? DeclaredType);
}