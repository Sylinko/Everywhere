using System.Text.Json;
using Everywhere.Harness.Providers.Gemini;

namespace Everywhere.Core.Tests.Harness;

/// <summary>
/// Verifies that <see cref="GeminiGenerateContentClient.TransformToOpenApi3Schema"/> resolves local references
/// and emits only Gemini-supported OpenAPI 3.0 keywords while retaining stripped constraints in descriptions.
/// </summary>
public sealed class GeminiToolSchemaTransformerTests
{
    [Test]
    public void TransformToOpenApi3Schema_WithReferencesAndUnsupportedKeywords_ProducesSelfContainedWhitelist()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "type": "object",
              "condition": "Duplicate function description",
              "properties": {
                "params": {
                  "$ref": "#/$defs/Input"
                },
                "nestedParams": {
                  "$ref": "#/properties/nestedParams/$defs/Input",
                  "$defs": {
                    "Input": {
                      "type": "object",
                      "properties": {
                        "path": { "type": "string" }
                      }
                    }
                  }
                },
                "data": {
                  "type": "object",
                  "propertyNames": { "type": "string" },
                  "additionalProperties": { "type": "string" }
                },
                "mode": {
                  "const": "fast"
                },
                "optionalTag": {
                  "type": [ "string", "null" ]
                }
              },
              "$defs": {
                "Input": {
                  "type": "object",
                  "properties": {
                    "query": {
                      "type": "string",
                      "exclusiveMinimum": 0
                    }
                  },
                  "required": [ "query" ]
                }
              }
            }
            """);

        var transformed = GeminiGenerateContentClient.TransformToOpenApi3Schema(schema);
        var properties = transformed.GetProperty("properties");
        var parameters = properties.GetProperty("params");
        var nestedParameters = properties.GetProperty("nestedParams");
        var data = properties.GetProperty("data");
        var optionalTag = properties.GetProperty("optionalTag");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transformed.TryGetProperty("$defs", out _), Is.False);
            Assert.That(transformed.TryGetProperty("condition", out _), Is.False);
            Assert.That(parameters.TryGetProperty("$ref", out _), Is.False);
            Assert.That(parameters.GetProperty("properties").GetProperty("query").GetProperty("type").GetString(), Is.EqualTo("string"));
            Assert.That(nestedParameters.TryGetProperty("$ref", out _), Is.False);
            Assert.That(nestedParameters.TryGetProperty("$defs", out _), Is.False);
            Assert.That(
                nestedParameters.GetProperty("properties").GetProperty("path").GetProperty("type").GetString(),
                Is.EqualTo("string"));
            Assert.That(
                parameters.GetProperty("properties").GetProperty("query").GetProperty("description").GetString(),
                Does.Contain("exclusiveMinimum=0"));
            Assert.That(data.TryGetProperty("propertyNames", out _), Is.False);
            Assert.That(data.TryGetProperty("additionalProperties", out _), Is.False);
            Assert.That(data.GetProperty("description").GetString(), Does.Contain("additionalProperties"));
            Assert.That(properties.GetProperty("mode").GetProperty("type").GetString(), Is.EqualTo("string"));
            Assert.That(properties.GetProperty("mode").GetProperty("enum")[0].GetString(), Is.EqualTo("fast"));
            Assert.That(optionalTag.GetProperty("type").GetString(), Is.EqualTo("string"));
            Assert.That(optionalTag.GetProperty("nullable").GetBoolean(), Is.True);
        }
    }

    [Test]
    public void TransformToOpenApi3Schema_WithRecursiveReference_BreaksCycleGracefully()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "type": "object",
              "properties": {
                "next": { "$ref": "#" }
              }
            }
            """);

        var transformed = GeminiGenerateContentClient.TransformToOpenApi3Schema(schema);
        var next = transformed.GetProperty("properties").GetProperty("next").GetProperty("properties").GetProperty("next");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(next.GetProperty("type").GetString(), Is.EqualTo("object"));
            Assert.That(next.GetProperty("description").GetString(), Is.EqualTo("Recursive schema omitted."));
        }
    }

    [Test]
    public void TransformToOpenApi3Schema_WithExternalReference_ThrowsInvalidOperationException()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "type": "object",
              "properties": {
                "external": { "$ref": "https://example.invalid/schema.json" }
              }
            }
            """);

        Assert.Throws<InvalidOperationException>(() => GeminiGenerateContentClient.TransformToOpenApi3Schema(schema));
    }
}
