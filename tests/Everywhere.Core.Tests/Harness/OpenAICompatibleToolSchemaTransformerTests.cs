using System.Text.Json;
using Everywhere.Harness;
using Microsoft.Extensions.AI;

namespace Everywhere.Core.Tests.Harness;

/// <summary>
/// Verifies the portable OpenAI-compatible tool schema whitelist, option cloning behavior, and local-reference
/// validation used immediately before Chat Completion requests are serialized.
/// </summary>
/// <example>
/// The fixture covers schemas such as <c>{ "const": "fast" }</c>, expecting a single-value <c>enum</c>, and a
/// dangling <c>#/$defs/Missing</c> reference, expecting a local validation exception before network I/O.
/// </example>
public class OpenAICompatibleToolSchemaTransformerTests
{
    [Test]
    public void TransformSchema_WithPortableAndUnsupportedKeywords_UsesPortableWhitelist()
    {
        var schema = ParseSchema(
            """
            {
              "type": "object",
              "condition": "Duplicate function description",
              "properties": {
                "params": { "$ref": "#/$defs/Input" },
                "nestedParams": {
                  "$ref": "#/properties/nestedParams/$defs/Input",
                  "$defs": {
                    "Input": {
                      "type": "object",
                      "properties": {
                        "query": { "type": "string" }
                      }
                    }
                  }
                },
                "data": {
                  "type": "object",
                  "propertyNames": { "type": "string" },
                  "additionalProperties": { "type": "string" },
                  "exclusiveMinimum": 0
                },
                "mode": { "const": "fast" },
                "variant": {
                  "oneOf": [
                    { "type": "string" },
                    { "type": "number" }
                  ]
                }
              },
              "$defs": {
                "Input": {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string" }
                  },
                  "required": [ "query" ]
                }
              }
            }
            """);

        var transformed = OpenAICompatibleToolSchemaTransformer.TransformSchema(schema, "test");
        var properties = transformed.GetProperty("properties");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transformed.TryGetProperty("condition", out _), Is.False);
            Assert.That(transformed.TryGetProperty("$defs", out _), Is.True);
            Assert.That(properties.GetProperty("params").GetProperty("$ref").GetString(), Is.EqualTo("#/$defs/Input"));
            Assert.That(
                properties.GetProperty("nestedParams").GetProperty("$ref").GetString(),
                Is.EqualTo("#/properties/nestedParams/$defs/Input"));
            Assert.That(properties.GetProperty("data").TryGetProperty("propertyNames", out _), Is.False);
            Assert.That(properties.GetProperty("data").TryGetProperty("additionalProperties", out _), Is.True);
            Assert.That(
                properties.GetProperty("data").GetProperty("description").GetString(),
                Does.Contain("exclusiveMinimum=0"));
            Assert.That(properties.GetProperty("mode").GetProperty("enum")[0].GetString(), Is.EqualTo("fast"));
            Assert.That(properties.GetProperty("mode").TryGetProperty("const", out _), Is.False);
            Assert.That(properties.GetProperty("variant").TryGetProperty("oneOf", out _), Is.False);
            Assert.That(properties.GetProperty("variant").GetProperty("anyOf").GetArrayLength(), Is.EqualTo(2));
        }
    }

    [Test]
    public void Transform_WithChangedToolSchema_ClonesOptionsAndPreservesOriginalFunction()
    {
        var function = new TestAIFunction(ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "value": { "const": 3 }
              }
            }
            """));
        var options = new ChatOptions
        {
            Tools = [function]
        };

        var transformed = OpenAICompatibleToolSchemaTransformer.Transform(options);
        var transformedFunction = (AIFunction)transformed!.Tools![0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transformed, Is.Not.SameAs(options));
            Assert.That(transformedFunction, Is.Not.SameAs(function));
            Assert.That(options.Tools![0], Is.SameAs(function));
            Assert.That(function.JsonSchema.GetProperty("properties").GetProperty("value").TryGetProperty("const", out _), Is.True);
            Assert.That(
                transformedFunction.JsonSchema.GetProperty("properties").GetProperty("value").GetProperty("enum")[0].GetInt32(),
                Is.EqualTo(3));
        }
    }

    [Test]
    public void TransformSchema_WithDanglingReference_ThrowsBeforeSending()
    {
        var schema = ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "params": { "$ref": "#/$defs/Missing" }
              }
            }
            """);

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAICompatibleToolSchemaTransformer.TransformSchema(schema, "broken_tool"));

        Assert.That(exception!.Message, Does.Contain("broken_tool").And.Contain("#/$defs/Missing"));
    }

    private static JsonElement ParseSchema(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>
    /// Minimal executable function used to prove that schema wrapping does not mutate the source function.
    /// </summary>
    /// <example>
    /// Constructing the helper with <c>{ "type": "object" }</c> exposes that exact value from
    /// <see cref="AIFunctionDeclaration.JsonSchema"/> and completes invocation with a <see langword="null"/> result.
    /// </example>
    private sealed class TestAIFunction(JsonElement jsonSchema) : AIFunction
    {
        public override string Name => "test";

        public override string Description => "Test function";

        public override JsonElement JsonSchema => jsonSchema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(null);
    }
}
