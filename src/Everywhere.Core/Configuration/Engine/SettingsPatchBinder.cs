using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using Everywhere.Common;
using Everywhere.Utilities;

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
    private readonly ISettingsDescriptorProvider _descriptorProvider;
    private readonly List<SettingsEngineDiagnostic> _diagnostics = [];

    /// <summary>
    /// Creates a binder with the generated descriptor provider when available.
    /// </summary>
    /// <param name="descriptorProvider">
    /// Optional provider used by tests or specialized hosts. When omitted, the
    /// source-generated provider is preferred and reflection is used as a
    /// compatibility fallback.
    /// </param>
    public SettingsPatchBinder(ISettingsDescriptorProvider? descriptorProvider = null)
    {
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
    /// <paramref name="observedPath"/> is an DeepObserver typed CLR path. This
    /// method resolves it through descriptors into a typed JSON path so numeric
    /// dictionary keys stay object properties while collection indexes become
    /// array segments. Path segments are never flattened into a delimiter string.
    /// When the path enters a serialized-subtree property or indexed value, the
    /// write is collapsed to that boundary and the current boundary value is
    /// read from <paramref name="rootValue"/>.
    /// </remarks>
    public void WriteObservedPath(
        JsonSettingsStorage store,
        ISettingsDescriptor rootDescriptor,
        object rootValue,
        DeepObserverPath observedPath,
        object? observedValue)
    {
        try
        {
            var resolution = ResolveObservedPath(rootDescriptor, rootValue, observedPath, observedValue);
            if (resolution.RuntimeValue is ISyncRoot syncRoot)
            {
                lock (syncRoot)
                {
                    WriteResolvedValue(store, resolution);
                }
            }
            else
            {
                WriteResolvedValue(store, resolution);
            }
        }
        catch (Exception ex)
        {
            var observedPathText = observedPath.ToString();
            var diagnostic = new SettingsEngineDiagnostic(
                SettingsEngineDiagnosticKind.WriteFailure,
                SettingsEngineDiagnosticSeverity.Error,
                observedPathText,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_WriteObservedPathFailed, observedPathText),
                ex);

            _diagnostics.Add(diagnostic);
            store.AddDiagnostic(diagnostic);
        }
    }

    private static void WriteResolvedValue(JsonSettingsStorage store, PathResolution resolution)
    {
        var valueType = resolution.DeclaredType ?? resolution.RuntimeValue?.GetType() ?? typeof(object);
        var node = SettingsEngineJson.SerializeToNode(resolution.RuntimeValue, valueType);
        store.ReplaceSubtree(resolution.JsonPath, node);
    }

    private void PatchObject(
        JsonObject json,
        ISettingsDescriptor descriptor,
        object target,
        string path,
        SettingsUnknownMemberHandling unknownMemberHandling,
        ISet<string>? initializedJsonNames = null)
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
            if (initializedJsonNames?.Contains(property.JsonName) is true)
            {
                continue;
            }

            PatchProperty(node, property, target, propertyPath);
        }

        HandleUnknownMembers(json, knownNames, path, unknownMemberHandling);
    }

    private void PatchProperty(JsonNode? node, ISettingsPropertyDescriptor property, object target, string path)
    {
        if (node is null)
        {
            PatchNullProperty(property, target, path);
            return;
        }

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

    private void PatchNullProperty(ISettingsPropertyDescriptor property, object target, string path)
    {
        var failureKind = property.Kind == SettingsPropertyKind.SerializedSubtree ?
            SettingsEngineDiagnosticKind.SerializedSubtreeFailure :
            SettingsEngineDiagnosticKind.ScalarConversionFailure;

        if (!property.Flags.HasFlag(SettingsPropertyFlags.AllowsNull))
        {
            AddNullabilityDiagnostic(failureKind, path);
            return;
        }

        if (property.Flags.HasFlag(SettingsPropertyFlags.CanWrite))
        {
            property.SetValue(target, null);
            return;
        }

        if (property.GetValue(target) is not null)
        {
            AddDiagnostic(
                SettingsEngineDiagnosticKind.UnsupportedShape,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_PropertyCannotBeSetToNull, path));
        }
    }

    private void PatchSerializedSubtree(JsonNode? node, ISettingsPropertyDescriptor property, object target, string path)
    {
        if (!property.Flags.HasFlag(SettingsPropertyFlags.CanWrite))
        {
            AddDiagnostic(
                SettingsEngineDiagnosticKind.SerializedSubtreeFailure,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_SerializedSubtreeNotWritable, path));
            return;
        }

        if (!TryDeserialize(node, GetValueContract(property), path, SettingsEngineDiagnosticKind.SerializedSubtreeFailure, out var value))
        {
            return;
        }

        property.SetValue(target, value);
    }

    private void PatchScalar(JsonNode? node, ISettingsPropertyDescriptor property, object target, string path)
    {
        if (!property.Flags.HasFlag(SettingsPropertyFlags.CanWrite))
        {
            return;
        }

        if (!TryDeserialize(node, GetValueContract(property), path, SettingsEngineDiagnosticKind.ScalarConversionFailure, out var value))
        {
            return;
        }

        property.SetValue(target, value);
    }

    private void PatchArray(JsonNode? node, ISettingsPropertyDescriptor property, object target, string path)
    {
        if (!property.Flags.HasFlag(SettingsPropertyFlags.CanWrite))
        {
            return;
        }

        if (node is JsonArray array && !ValidateCollectionElements(array, GetElementContract(property), path))
        {
            return;
        }

        if (!TryDeserialize(node, GetValueContract(property), path, SettingsEngineDiagnosticKind.ScalarConversionFailure, out var value))
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
            var elementContract = GetElementContract(property);
            var commonCount = Math.Min(list.Count, array.Count);

            // Keep the collection object stable for UI bindings and observers.
            // Existing object items are patched in place by index; terminal values
            // are replaced at the same index only after successful conversion.
            for (var i = 0; i < commonCount; i++)
            {
                var entryPath = CombinePath(path, i.ToString(CultureInfo.InvariantCulture));
                var result = PatchValue(
                    array[i],
                    elementContract,
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
                        elementContract,
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

        if (!property.Flags.HasFlag(SettingsPropertyFlags.CanWrite))
        {
            AddDiagnostic(
                SettingsEngineDiagnosticKind.UnsupportedShape,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_ListNotMutable, path));
            return;
        }

        var replacementType = typeof(List<>).MakeGenericType(property.ElementType ?? typeof(object));
        if (property.PropertyType.IsAssignableFrom(replacementType))
        {
            if (TryCreateListReplacement(
                    array,
                    replacementType,
                    GetElementContract(property),
                    path,
                    out var listReplacement))
            {
                property.SetValue(target, listReplacement);
            }

            return;
        }

        if (!ValidateCollectionElements(array, GetElementContract(property), path))
        {
            return;
        }

        if (TryDeserialize(node, GetValueContract(property), path, SettingsEngineDiagnosticKind.ScalarConversionFailure, out var replacement))
        {
            property.SetValue(target, replacement);
        }
    }

    private bool TryCreateListReplacement(
        JsonArray array,
        Type replacementType,
        ValueContract elementContract,
        string path,
        out object? replacement)
    {
        replacement = null;

        if (Activator.CreateInstance(replacementType) is not IList list)
        {
            return false;
        }

        for (var i = 0; i < array.Count; i++)
        {
            var entryPath = CombinePath(path, i.ToString(CultureInfo.InvariantCulture));
            if (!TryCreateValue(array[i], elementContract, entryPath, out var item))
            {
                return false;
            }

            list.Add(item);
        }

        replacement = list;
        return true;
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
            var valueContract = GetElementContract(property);
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
                        valueContract,
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

                if (TryCreateValue(pair.Value, valueContract, entryPath, out var value))
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

        if (!property.Flags.HasFlag(SettingsPropertyFlags.CanWrite))
        {
            AddDiagnostic(
                SettingsEngineDiagnosticKind.UnsupportedShape,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_DictionaryNotMutable, path));
            return;
        }

        if (!ValidateDictionaryValues(obj, GetElementContract(property), path))
        {
            return;
        }

        if (TryDeserialize(node, GetValueContract(property), path, SettingsEngineDiagnosticKind.ScalarConversionFailure, out var replacement))
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
            if (pair.Value is null)
            {
                if (!property.Flags.HasFlag(SettingsPropertyFlags.ElementAllowsNull))
                {
                    AddNullabilityDiagnostic(SettingsEngineDiagnosticKind.ScalarConversionFailure, entryPath);
                }
                else if (existingValue is not null)
                {
                    AddDiagnostic(
                        SettingsEngineDiagnosticKind.UnsupportedShape,
                        SettingsEngineDiagnosticSeverity.Warning,
                        entryPath,
                        DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_PropertyCannotBeSetToNull, entryPath));
                }

                continue;
            }

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
        ValueContract contract,
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

        return TryCreateValue(node, contract, path, failureKind, out replacement) ?
            ValuePatchResult.Replace :
            ValuePatchResult.Failed;
    }

    private bool TryCreateValue(JsonNode? node, ValueContract contract, string path, out object? value) =>
        TryCreateValue(node, contract, path, SettingsEngineDiagnosticKind.ScalarConversionFailure, out value);

    private bool TryCreateValue(
        JsonNode? node,
        ValueContract contract,
        string path,
        SettingsEngineDiagnosticKind failureKind,
        out object? value)
    {
        value = null;
        var normalizedType = Nullable.GetUnderlyingType(contract.Type) ?? contract.Type;

        if (node is JsonObject obj &&
            TryGetPatchDescriptor(normalizedType, out var descriptor) &&
            TryCreatePatchableObject(obj, descriptor, path, out value))
        {
            return true;
        }

        return TryDeserialize(node, contract, path, failureKind, out value);
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
                !property.Flags.HasFlag(SettingsPropertyFlags.CanWrite) ||
                !TryCreatePatchableObject(obj, childDescriptor, path, out value))
            {
                AddDiagnostic(
                    SettingsEngineDiagnosticKind.UnsupportedShape,
                    SettingsEngineDiagnosticSeverity.Warning,
                    path,
                    DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_ObjectCannotBeCreated, path));
                return;
            }

            property.SetValue(target, value);
            return;
        }

        if (property.ChildDescriptor is { } descriptor)
        {
            PatchObject(obj, descriptor, value, path, property.UnknownMemberHandling);
        }
    }

    private bool TryCreatePatchableObject(JsonObject obj, ISettingsDescriptor descriptor, string path, out object? value)
    {
        var binding = BindCreationInitializer(obj, descriptor, path);
        if (!descriptor.TryCreateInstance(binding.InitialValues, out value))
        {
            return false;
        }

        if (value is null)
        {
            return false;
        }

        PatchObject(obj, descriptor, value, path, descriptor.UnknownMemberHandling, binding.InitializedJsonNames);
        return true;
    }

    private CreationInitializerBinding BindCreationInitializer(JsonObject obj, ISettingsDescriptor descriptor, string path)
    {
        Dictionary<string, object?>? values = null;
        HashSet<string>? initializedJsonNames = null;

        foreach (var property in descriptor.Properties)
        {
            if (!property.Flags.HasFlag(SettingsPropertyFlags.CanInitialize) ||
                !obj.TryGetPropertyValue(property.JsonName, out var node))
            {
                continue;
            }

            var propertyPath = CombinePath(path, property.JsonName);
            (initializedJsonNames ??= new HashSet<string>(StringComparer.Ordinal)).Add(property.JsonName);
            if (!TryBindInitializerValue(node, property, propertyPath, out var value))
            {
                continue;
            }

            (values ??= new Dictionary<string, object?>(StringComparer.Ordinal))[property.JsonName] = value;
        }

        return new CreationInitializerBinding(values, initializedJsonNames);
    }

    private bool TryBindInitializerValue(
        JsonNode? node,
        ISettingsPropertyDescriptor property,
        string path,
        out object? value)
    {
        value = null;

        if (property is { Kind: SettingsPropertyKind.Object, ChildDescriptor: { } descriptor })
        {
            if (node is null)
            {
                return TryDeserialize(
                    node,
                    GetValueContract(property),
                    path,
                    SettingsEngineDiagnosticKind.ScalarConversionFailure,
                    out value);
            }

            if (node is JsonObject obj)
            {
                return TryCreatePatchableObject(obj, descriptor, path, out value);
            }

            AddDiagnostic(
                SettingsEngineDiagnosticKind.ScalarConversionFailure,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_ExpectedJsonObjectForObject, path));
            return false;
        }

        return TryDeserialize(
            node,
            GetValueContract(property),
            path,
            property.Kind == SettingsPropertyKind.SerializedSubtree ?
                SettingsEngineDiagnosticKind.SerializedSubtreeFailure :
                SettingsEngineDiagnosticKind.ScalarConversionFailure,
            out value);
    }

    private bool ValidateCollectionElements(JsonArray array, ValueContract elementContract, string path)
    {
        if (elementContract.AllowsNull)
        {
            return true;
        }

        var isValid = true;
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not null)
            {
                continue;
            }

            AddNullabilityDiagnostic(
                SettingsEngineDiagnosticKind.ScalarConversionFailure,
                CombinePath(path, i.ToString(CultureInfo.InvariantCulture)));
            isValid = false;
        }

        return isValid;
    }

    private bool ValidateDictionaryValues(JsonObject obj, ValueContract valueContract, string path)
    {
        if (valueContract.AllowsNull)
        {
            return true;
        }

        var isValid = true;
        foreach (var pair in obj)
        {
            if (pair.Value is not null)
            {
                continue;
            }

            AddNullabilityDiagnostic(
                SettingsEngineDiagnosticKind.ScalarConversionFailure,
                CombinePath(path, pair.Key));
            isValid = false;
        }

        return isValid;
    }

    private static ValueContract GetValueContract(ISettingsPropertyDescriptor property) =>
        new(property.PropertyType, property.Flags.HasFlag(SettingsPropertyFlags.AllowsNull));

    private static ValueContract GetElementContract(ISettingsPropertyDescriptor property) =>
        new(property.ElementType ?? typeof(object), property.Flags.HasFlag(SettingsPropertyFlags.ElementAllowsNull));

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
        ValueContract contract,
        string path,
        SettingsEngineDiagnosticKind failureKind,
        out object? value)
    {
        value = null;

        if (node is null)
        {
            if (contract.AllowsNull)
            {
                return true;
            }

            AddNullabilityDiagnostic(failureKind, path);
            return false;
        }

        try
        {
            value = SettingsEngineJson.Deserialize(node, contract.Type);
            return true;
        }
        catch (Exception ex)
        {
            AddDiagnostic(
                failureKind,
                SettingsEngineDiagnosticSeverity.Warning,
                path,
                DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_ReadValueFailed, path, contract.Type.Name),
                ex);
            return false;
        }
    }

    private void AddNullabilityDiagnostic(SettingsEngineDiagnosticKind failureKind, string path) =>
        AddDiagnostic(
            failureKind,
            SettingsEngineDiagnosticSeverity.Warning,
            path,
            DiagnosticMessage(LocaleKey.SettingsEngine_Diagnostic_NullForNonNullable, path));

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

    private PathResolution ResolveObservedPath(
        ISettingsDescriptor rootDescriptor,
        object rootValue,
        DeepObserverPath observedPath,
        object? observedValue)
    {
        var jsonSegments = new List<SettingsJsonPathSegment>();
        var currentDescriptor = rootDescriptor;
        object? currentValue = rootValue;
        ISettingsDescriptor? indexedValueDescriptor = null;
        Type? indexedValueType = null;
        ISettingsPropertyDescriptor? indexedProperty = null;

        var declaredType = rootDescriptor.Type;
        var expectingListIndex = false;
        var expectingDictionaryKey = false;

        foreach (var segment in observedPath.AsSpan())
        {
            if (expectingListIndex)
            {
                if (segment.Kind != DeepObserverPathSegmentKind.CollectionIndex)
                {
                    throw new InvalidOperationException(
                        $"Observed settings path segment '{segment}' must be a collection index.");
                }

                var index = segment.Index;
                jsonSegments.Add(SettingsJsonPathSegment.Index(index));
                declaredType = indexedValueType ?? declaredType;
                currentValue = currentValue is IList list && index < list.Count ? list[index] : observedValue;
                currentDescriptor = indexedValueDescriptor ?? currentDescriptor;
                expectingListIndex = false;

                if (indexedValueDescriptor?.IsSerializedSubtree is true)
                {
                    return new PathResolution(new SettingsJsonPath(jsonSegments), declaredType, currentValue);
                }

                continue;
            }

            if (expectingDictionaryKey)
            {
                if (segment.Kind != DeepObserverPathSegmentKind.DictionaryKey || segment.Name is not { } keyText)
                {
                    throw new InvalidOperationException(
                        $"Observed settings path segment '{segment}' must be a dictionary key.");
                }

                jsonSegments.Add(SettingsJsonPathSegment.Property(keyText));
                declaredType = indexedValueType ?? declaredType;
                var key = indexedProperty?.DictionaryKeyReader?.Invoke(keyText);
                currentValue = key is not null && currentValue is IDictionary dictionary && dictionary.Contains(key) ?
                    dictionary[key] :
                    observedValue;
                currentDescriptor = indexedValueDescriptor ?? currentDescriptor;
                expectingDictionaryKey = false;

                if (indexedValueDescriptor?.IsSerializedSubtree is true)
                {
                    return new PathResolution(new SettingsJsonPath(jsonSegments), declaredType, currentValue);
                }

                continue;
            }

            if (segment.Kind != DeepObserverPathSegmentKind.Property || segment.Name is not { } propertyName)
            {
                throw new InvalidOperationException(
                    $"Observed settings path segment '{segment}' must be a property name.");
            }

            if (currentDescriptor.FindProperty(propertyName) is { } property)
            {
                jsonSegments.Add(SettingsJsonPathSegment.Property(property.JsonName));
                declaredType = property.PropertyType;
                currentValue = currentValue is null ? observedValue : property.GetValue(currentValue);

                if (property.Kind == SettingsPropertyKind.SerializedSubtree)
                {
                    return new PathResolution(new SettingsJsonPath(jsonSegments), declaredType, currentValue);
                }

                if (property.Kind is SettingsPropertyKind.Array or SettingsPropertyKind.List)
                {
                    indexedValueType = property.ElementType;
                    indexedValueDescriptor = GetChildDescriptor(indexedValueType);
                    indexedProperty = property;
                    expectingListIndex = true;
                }
                else if (property.Kind == SettingsPropertyKind.Dictionary)
                {
                    indexedValueType = property.DictionaryValueType;
                    indexedValueDescriptor = GetChildDescriptor(indexedValueType);
                    indexedProperty = property;
                    expectingDictionaryKey = true;
                }
                else
                {
                    indexedValueType = null;
                    indexedValueDescriptor = null;
                    indexedProperty = null;
                    currentDescriptor = property.ChildDescriptor ?? currentDescriptor;
                }

                continue;
            }

            jsonSegments.Add(SettingsJsonPathSegment.Property(propertyName));
            declaredType = indexedValueType ?? declaredType;
            currentDescriptor = indexedValueDescriptor ?? currentDescriptor;
            currentValue = observedValue;
        }

        return new PathResolution(new SettingsJsonPath(jsonSegments), declaredType, currentValue);
    }

    private ISettingsDescriptor? GetChildDescriptor(Type? type)
    {
        if (type is null) return null;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (ReflectionSettingsDescriptorProvider.IsScalarType(type) &&
            !type.IsDefined(typeof(SettingsSerializedSubtreeAttribute), inherit: true))
        {
            return null;
        }

        if (ReflectionSettingsDescriptorProvider.TryGetListElementType(type, out _)) return null;
        if (ReflectionSettingsDescriptorProvider.TryGetDictionaryTypes(type, out _, out _)) return null;

        var descriptor = _descriptorProvider.GetDescriptor(type);
        if (descriptor.IsSerializedSubtree) return descriptor;
        if (type.IsAbstract || type.IsInterface) return null;
        return descriptor;
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

    private static string CombinePath(string prefix, string segment) =>
        string.IsNullOrEmpty(prefix) ? segment : $"{prefix}.{segment}";

    private enum ValuePatchResult
    {
        Failed,
        PatchedInPlace,
        Replace
    }

    private readonly record struct ValueContract(Type Type, bool AllowsNull);

    private readonly record struct CreationInitializerBinding(
        IReadOnlyDictionary<string, object?>? InitialValues,
        ISet<string>? InitializedJsonNames
    );

    private readonly record struct PathResolution(SettingsJsonPath JsonPath, Type? DeclaredType, object? RuntimeValue);
}