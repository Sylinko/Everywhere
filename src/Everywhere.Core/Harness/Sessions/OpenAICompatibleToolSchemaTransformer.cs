using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.AI;
using Microsoft.Extensions.AI;

namespace Everywhere.Harness;

/// <summary>
/// Converts tool parameter schemas to a conservative JSON Schema subset suitable for OpenAI Chat Completion
/// compatible endpoints while retaining enough information to guide tool argument generation.
/// </summary>
/// <remarks>
/// The transformer is endpoint-agnostic: it is applied to every connection using
/// <see cref="ModelProviderSchema.OpenAI"/> and does not contain provider-specific branches. It clones
/// <see cref="ChatOptions"/> only when at least one tool schema changes, wraps functions without changing their
/// invocation behavior, preserves complete local <c>$defs</c>/<c>$ref</c> graphs, and rejects dangling or external
/// references before a request is sent. Unsupported validation keywords are either lowered to portable equivalents
/// (for example, <c>const</c> to a single-value <c>enum</c>) or recorded in <c>description</c> when server-side
/// enforcement cannot be expressed safely.
/// </remarks>
/// <example>
/// Given this provider-specific fragment:
/// <code>
/// {
///   "type": "object",
///   "properties": {
///     "mode": { "const": "fast" },
///     "data": {
///       "type": "object",
///       "propertyNames": { "type": "string" },
///       "additionalProperties": { "type": "string" }
///     }
///   }
/// }
/// </code>
/// the outgoing schema contains <c>"enum": ["fast"]</c>, removes the redundant
/// <c>propertyNames: { type: string }</c>, and preserves the <c>additionalProperties</c> value schema.
/// </example>
internal static class OpenAICompatibleToolSchemaTransformer
{
    private static readonly HashSet<string> DescriptionOnlyKeywords =
    [
        "default",
        "dependentRequired",
        "exclusiveMaximum",
        "exclusiveMinimum",
        "format",
        "if",
        "maxContains",
        "maxItems",
        "maxLength",
        "maxProperties",
        "maximum",
        "minContains",
        "minItems",
        "minLength",
        "minProperties",
        "minimum",
        "multipleOf",
        "not",
        "pattern",
        "then",
        "uniqueItems",
        "else",
    ];

    public static ChatOptions? Transform(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 } tools)
        {
            return options;
        }

        var transformedTools = new List<AITool>(tools.Count);
        var hasChanges = false;
        foreach (var tool in tools)
        {
            switch (tool)
            {
                case AIFunction function:
                {
                    var schema = TransformSchema(function.JsonSchema, function.Name);
                    if (schema.GetRawText() == function.JsonSchema.GetRawText())
                    {
                        transformedTools.Add(function);
                    }
                    else
                    {
                        transformedTools.Add(new SchemaTransformedAIFunction(function, schema));
                        hasChanges = true;
                    }

                    break;
                }
                case AIFunctionDeclaration declaration:
                {
                    var schema = TransformSchema(declaration.JsonSchema, declaration.Name);
                    if (schema.GetRawText() == declaration.JsonSchema.GetRawText())
                    {
                        transformedTools.Add(declaration);
                    }
                    else
                    {
                        transformedTools.Add(new SchemaTransformedAIFunctionDeclaration(declaration, schema));
                        hasChanges = true;
                    }

                    break;
                }
                default:
                {
                    transformedTools.Add(tool);
                    break;
                }
            }
        }

        if (!hasChanges)
        {
            return options;
        }

        var transformedOptions = options.Clone();
        transformedOptions.Tools = transformedTools;
        return transformedOptions;
    }

    internal static JsonElement TransformSchema(JsonElement schema, string functionName)
    {
        var source = JsonNode.Parse(schema.GetRawText());
        var transformed = TransformSchemaNode(source) ?? new JsonObject();
        ValidateReferences(transformed, functionName);
        return JsonSerializer.SerializeToElement(transformed);
    }

    private static JsonNode? TransformSchemaNode(JsonNode? node)
    {
        if (node is not JsonObject schema)
        {
            return node?.DeepClone();
        }

        var result = new JsonObject();
        var constraintDescriptions = new List<string>();
        JsonNode? constant = null;
        var nullable = false;

        foreach (var property in schema)
        {
            switch (property.Key)
            {
                case "type":
                case "description":
                case "enum":
                case "required":
                case "$ref":
                {
                    result[property.Key] = property.Value?.DeepClone();
                    break;
                }
                case "properties":
                case "$defs":
                case "definitions":
                {
                    result[property.Key] = TransformSchemaMap(property.Value);
                    break;
                }
                case "items":
                case "additionalProperties":
                {
                    result[property.Key] = TransformSchemaNode(property.Value);
                    break;
                }
                case "allOf":
                {
                    result[property.Key] = TransformSchemaArray(property.Value);
                    break;
                }
                case "anyOf":
                {
                    MergeAnyOf(result, TransformSchemaArray(property.Value));
                    break;
                }
                case "oneOf":
                {
                    MergeAnyOf(result, TransformSchemaArray(property.Value));
                    break;
                }
                case "const":
                {
                    constant = property.Value?.DeepClone();
                    break;
                }
                case "nullable":
                {
                    nullable = property.Value is JsonValue value && value.TryGetValue<bool>(out var isNullable) && isNullable;
                    break;
                }
                case "propertyNames":
                {
                    if (!IsUnconstrainedStringSchema(property.Value))
                    {
                        constraintDescriptions.Add($"propertyNames={GetCompactJson(property.Value)}");
                    }

                    break;
                }
                case "condition":
                {
                    break;
                }
                default:
                {
                    if (DescriptionOnlyKeywords.Contains(property.Key))
                    {
                        constraintDescriptions.Add($"{property.Key}={GetCompactJson(property.Value)}");
                    }

                    break;
                }
            }
        }

        if (constant is not null)
        {
            result["enum"] = new JsonArray(constant);
        }

        if (nullable)
        {
            MakeNullable(result);
        }

        if (constraintDescriptions.Count > 0)
        {
            AppendDescription(result, $"Constraints: {string.Join(", ", constraintDescriptions)}.");
        }

        return result;
    }

    private static void MergeAnyOf(JsonObject schema, JsonNode? alternatives)
    {
        if (schema.TryGetPropertyValue("anyOf", out var anyOf) &&
            anyOf is JsonArray anyOfArray &&
            alternatives is JsonArray alternativesArray)
        {
            foreach (var item in alternativesArray)
            {
                anyOfArray.Add(item?.DeepClone());
            }
        }
        else
        {
            schema["anyOf"] = alternatives;
        }
    }

    private static JsonNode? TransformSchemaMap(JsonNode? node)
    {
        if (node is not JsonObject map)
        {
            return node?.DeepClone();
        }

        var result = new JsonObject();
        foreach (var property in map)
        {
            result[property.Key] = TransformSchemaNode(property.Value);
        }

        return result;
    }

    private static JsonNode? TransformSchemaArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return node?.DeepClone();
        }

        var result = new JsonArray();
        foreach (var item in array)
        {
            result.Add(TransformSchemaNode(item));
        }

        return result;
    }

    private static void MakeNullable(JsonObject schema)
    {
        if (schema.TryGetPropertyValue("type", out var typeNode))
        {
            if (typeNode is JsonValue typeValue &&
                typeValue.TryGetValue<string>(out var type) &&
                type != "null")
            {
                schema["type"] = new JsonArray(type, "null");
            }
            else if (typeNode is JsonArray typeArray &&
                     !typeArray.AsValueEnumerable().Any(itemValue =>
                         itemValue is JsonValue value && value.TryGetValue<string>(out var itemType) && itemType == "null"))
            {
                typeArray.Add("null");
            }

            return;
        }

        if (schema.TryGetPropertyValue("anyOf", out var anyOfNode) && anyOfNode is JsonArray anyOf)
        {
            anyOf.Add(new JsonObject { ["type"] = "null" });
        }
    }

    private static bool IsUnconstrainedStringSchema(JsonNode? node) =>
        node is JsonObject { Count: 1 } schema &&
        schema.TryGetPropertyValue("type", out var typeNode) &&
        typeNode is JsonValue typeValue &&
        typeValue.TryGetValue<string>(out var type) &&
        type == "string";

    private static string GetCompactJson(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static void AppendDescription(JsonObject schema, string text)
    {
        if (schema.TryGetPropertyValue("description", out var descriptionNode) &&
            descriptionNode is JsonValue descriptionValue &&
            descriptionValue.TryGetValue<string>(out var description) &&
            !description.IsNullOrWhiteSpace())
        {
            schema["description"] = $"{description} {text}";
        }
        else
        {
            schema["description"] = text;
        }
    }

    private static void ValidateReferences(JsonNode schema, string functionName)
    {
        foreach (var reference in EnumerateReferences(schema))
        {
            if (!TryResolveReference(schema, reference))
            {
                throw new InvalidOperationException(
                    $"Tool '{functionName}' contains an unresolved or external JSON schema reference: '{reference}'.");
            }
        }
    }

    private static IEnumerable<string> EnumerateReferences(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("$ref", out var referenceNode) &&
                referenceNode is JsonValue referenceValue &&
                referenceValue.TryGetValue<string>(out var reference))
            {
                yield return reference;
            }

            foreach (var property in obj)
            {
                foreach (var nestedReference in EnumerateReferences(property.Value))
                {
                    yield return nestedReference;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                foreach (var nestedReference in EnumerateReferences(item))
                {
                    yield return nestedReference;
                }
            }
        }
    }

    private static bool TryResolveReference(JsonNode root, string reference)
    {
        if (reference == "#")
        {
            return true;
        }

        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return false;
        }

        var current = root;
        foreach (var encodedSegment in reference.AsSpan(2).ToString().Split('/'))
        {
            var segment = Uri.UnescapeDataString(encodedSegment)
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (current is JsonObject obj && obj.TryGetPropertyValue(segment, out current))
            {
                continue;
            }

            if (current is JsonArray array &&
                int.TryParse(segment, out var index) &&
                index >= 0 &&
                index < array.Count)
            {
                current = array[index];
                continue;
            }

            return false;
        }

        return current is not null;
    }

    /// <summary>
    /// Wraps an executable function with its transformed wire schema while delegating invocation and service lookup
    /// to the original function.
    /// </summary>
    /// <example>
    /// If an inner function declares <c>{ "const": 3 }</c>, this wrapper exposes
    /// <c>{ "enum": [3] }</c> through <see cref="AIFunctionDeclaration.JsonSchema"/> while invoking the same inner
    /// function instance.
    /// </example>
    private sealed class SchemaTransformedAIFunction(AIFunction innerFunction, JsonElement jsonSchema) : DelegatingAIFunction(innerFunction)
    {
        public override JsonElement JsonSchema => jsonSchema;
    }

    /// <summary>
    /// Wraps a non-executable function declaration with a transformed wire schema and delegates all remaining
    /// declaration metadata and service resolution to the original declaration.
    /// </summary>
    /// <example>
    /// A declaration using <c>oneOf</c> can be exposed as an equivalent portable <c>anyOf</c> declaration without
    /// replacing its name, description, return schema, or additional properties.
    /// </example>
    private sealed class SchemaTransformedAIFunctionDeclaration(AIFunctionDeclaration innerFunction, JsonElement jsonSchema) : AIFunctionDeclaration
    {
        public override string Name => innerFunction.Name;

        public override string Description => innerFunction.Description;

        public override JsonElement JsonSchema => jsonSchema;

        public override JsonElement? ReturnJsonSchema => innerFunction.ReturnJsonSchema;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => innerFunction.AdditionalProperties;

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            innerFunction.GetService(serviceType, serviceKey);
    }
}
