using System.Text.Json;
using System.Text.Json.Nodes;

namespace Everywhere.Harness.Providers.Gemini;

partial class GeminiGenerateContentClient
{
    /// <summary>
    /// Transforms a JSON Schema produced by MEAI or MCP tools into Gemini <c>generateContent</c>'s
    /// OpenAPI 3.0 subset: resolves local <c>#/...</c> references, converts nullable type arrays and <c>const</c>,
    /// removes unsupported keywords, and records stripped constraints in <c>description</c>.
    /// </summary>
    public static JsonElement TransformToOpenApi3Schema(JsonElement jsonElement)
    {
        var node = JsonNode.Parse(jsonElement.GetRawText());
        if (node is not JsonObject rootObject) return JsonSerializer.SerializeToElement(node);

        var referenceRoot = (JsonObject)rootObject.DeepClone();
        ResolveReferences(rootObject, referenceRoot, []);
        TransformOpenApi3Object(rootObject);
        return JsonSerializer.SerializeToElement(node);

        static void TransformOpenApi3Object(JsonObject obj)
        {
            var constraints = new List<string>();

            if (obj.TryGetPropertyValue("additionalProperties", out var additionalPropertiesNode))
            {
                obj.Remove("additionalProperties");
                if (additionalPropertiesNode is JsonValue additionalPropertiesValue &&
                    additionalPropertiesValue.TryGetValue(out bool allowsAdditionalProperties))
                {
                    if (allowsAdditionalProperties)
                    {
                        constraints.Add($"additionalProperties={additionalPropertiesNode.ToJsonString()}");
                    }
                }
                else if (additionalPropertiesNode is not null)
                {
                    constraints.Add($"additionalProperties={additionalPropertiesNode.ToJsonString()}");
                }
            }

            if (obj.TryGetPropertyValue("propertyNames", out var propertyNamesNode))
            {
                obj.Remove("propertyNames");
                if (propertyNamesNode is not JsonObject { Count: 1 } propertyNamesObject ||
                    !propertyNamesObject.TryGetPropertyValue("type", out var propertyNamesTypeNode) ||
                    propertyNamesTypeNode?.GetValue<string>() != "string")
                {
                    constraints.Add($"propertyNames={propertyNamesNode?.ToJsonString() ?? "null"}");
                }
            }

            if (obj.TryGetPropertyValue("const", out var constNode))
            {
                obj.Remove("const");
                obj["enum"] = new JsonArray(constNode?.DeepClone());
            }

            if (obj.TryGetPropertyValue("oneOf", out var oneOfNode))
            {
                obj.Remove("oneOf");
                if (!obj.ContainsKey("anyOf"))
                {
                    obj["anyOf"] = oneOfNode;
                }
            }

            // Convert nullable type arrays (e.g. ["string", "null"]) to OpenAPI 3.0 "type" + "nullable": true,
            // and default untyped enum schemas to "string" when missing.
            if (obj.TryGetPropertyValue("enum", out _) && !obj.ContainsKey("type"))
            {
                obj["type"] = JsonValue.Create("string");
            }
            else if (obj.TryGetPropertyValue("type", out var typeNode) && typeNode is JsonArray typeArray)
            {
                var types = typeArray.Select(static t => t?.GetValue<string>()).Where(static t => t is not null).ToList();
                if (types.Contains("null"))
                {
                    if (types.FirstOrDefault(static t => t != "null") is { } mainType)
                    {
                        obj["type"] = JsonValue.Create(mainType);
                    }
                    else
                    {
                        obj.Remove("type");
                    }

                    obj["nullable"] = JsonValue.Create(true);
                }
            }

            if (obj.TryGetPropertyValue("properties", out var propsNode) && propsNode is JsonObject properties)
            {
                foreach (var property in properties)
                {
                    if (property.Value is JsonObject propertyObj)
                    {
                        TransformOpenApi3Object(propertyObj);
                    }
                }
            }

            if (obj.TryGetPropertyValue("items", out var itemsNode) && itemsNode is JsonObject itemsObj)
            {
                TransformOpenApi3Object(itemsObj);
            }

            if (obj.TryGetPropertyValue("anyOf", out var rootAnyOfNode) && rootAnyOfNode is JsonArray rootAnyOfArray)
            {
                foreach (var anyOfObj in rootAnyOfArray.OfType<JsonObject>())
                {
                    TransformOpenApi3Object(anyOfObj);
                }
            }

            foreach (var property in obj.ToList())
            {
                if (IsSupportedSchemaKeyword(property.Key))
                {
                    continue;
                }

                obj.Remove(property.Key);
                if (IsDescriptionOnlySchemaKeyword(property.Key))
                {
                    constraints.Add($"{property.Key}={property.Value?.ToJsonString() ?? "null"}");
                }
            }

            if (constraints.Count == 0) return;

            var constraintDescription = $"Constraints: {string.Join(", ", constraints)}.";
            obj["description"] = obj.TryGetPropertyValue("description", out var descriptionNode) &&
                descriptionNode is JsonValue descriptionValue &&
                descriptionValue.TryGetValue(out string? description) &&
                !string.IsNullOrWhiteSpace(description)
                    ? $"{description} {constraintDescription}"
                    : constraintDescription;
        }

        static void ResolveReferences(JsonNode? node, JsonObject referenceRoot, HashSet<string> referenceStack)
        {
            if (node is JsonObject obj)
            {
                if (obj.TryGetPropertyValue("$ref", out var referenceNode) &&
                    referenceNode is JsonValue referenceValue &&
                    referenceValue.TryGetValue(out string? reference))
                {
                    if (!TryResolveReference(referenceRoot, reference, out var referencedNode))
                    {
                        throw new InvalidOperationException($"Unresolved or external JSON schema reference: '{reference}'.");
                    }

                    if (!referenceStack.Add(reference))
                    {
                        obj.Remove("$ref");
                        obj.TryAdd("type", JsonValue.Create("object"));
                        obj["description"] = "Recursive schema omitted.";
                        return;
                    }

                    if (referencedNode is JsonObject referencedObject)
                    {
                        var resolvedObject = (JsonObject)referencedObject.DeepClone();
                        foreach (var property in obj.Where(static p => p.Key != "$ref"))
                        {
                            resolvedObject[property.Key] = property.Value?.DeepClone();
                        }

                        obj.Clear();
                        foreach (var property in resolvedObject)
                        {
                            obj[property.Key] = property.Value?.DeepClone();
                        }

                        ResolveReferences(obj, referenceRoot, referenceStack);
                    }

                    referenceStack.Remove(reference);
                }

                foreach (var property in obj.ToList())
                {
                    ResolveReferences(property.Value, referenceRoot, referenceStack);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    ResolveReferences(item, referenceRoot, referenceStack);
                }
            }
        }

        static bool TryResolveReference(JsonNode root, string reference, out JsonNode? referencedNode)
        {
            referencedNode = root;
            if (reference == "#")
            {
                return true;
            }

            if (!reference.StartsWith("#/", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var encodedSegment in reference[2..].Split('/'))
            {
                var segment = Uri.UnescapeDataString(encodedSegment)
                    .Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal);
                if (referencedNode is JsonObject obj && obj.TryGetPropertyValue(segment, out referencedNode))
                {
                    continue;
                }

                if (referencedNode is JsonArray array &&
                    int.TryParse(segment, out var index) &&
                    index >= 0 &&
                    index < array.Count)
                {
                    referencedNode = array[index];
                    continue;
                }

                referencedNode = null;
                return false;
            }

            return referencedNode is not null;
        }

        static bool IsSupportedSchemaKeyword(string keyword) => keyword is
            "type" or
            "format" or
            "description" or
            "nullable" or
            "enum" or
            "maxItems" or
            "minItems" or
            "properties" or
            "required" or
            "propertyOrdering" or
            "items" or
            "minimum" or
            "maximum" or
            "anyOf";

        static bool IsDescriptionOnlySchemaKeyword(string keyword) => keyword is
            "allOf" or
            "default" or
            "dependentRequired" or
            "exclusiveMaximum" or
            "exclusiveMinimum" or
            "if" or
            "maxContains" or
            "maxLength" or
            "maxProperties" or
            "minContains" or
            "minLength" or
            "minProperties" or
            "multipleOf" or
            "not" or
            "pattern" or
            "then" or
            "uniqueItems" or
            "else";
    }
}
