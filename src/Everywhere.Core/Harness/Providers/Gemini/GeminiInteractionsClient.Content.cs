using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Everywhere.Harness.Providers.Gemini;

partial class GeminiInteractionsClient
{
    private static JsonNode EncodeResult(object? result)
    {
        if (result is IReadOnlyList<AIContent> parts)
        {
            var blocks = new JsonArray();
            foreach (var part in parts)
            {
                var block = EncodeContent(part);
                if (block["type"]?.GetValue<string>() is not ("text" or "image"))
                {
                    throw new NotSupportedException("Gemini function result blocks must be text or images.");
                }

                blocks.Add(block);
            }

            return blocks;
        }

        var value = JsonSerializer.SerializeToNode(result);
        if (value is JsonObject || value?.GetValueKind() == JsonValueKind.String)
        {
            return value;
        }

        return JsonValue.Create(value?.ToJsonString() ?? "null");
    }

    private static JsonObject EncodeContent(AIContent content)
    {
        if (content is TextContent text)
        {
            return new JsonObject { ["type"] = "text", ["text"] = text.Text };
        }

        var mediaType = content switch
        {
            DataContent data => data.MediaType,
            UriContent uri => uri.MediaType,
            _ => throw new NotSupportedException($"Unsupported Gemini content: {content.GetType().Name}.")
        };
        var type = mediaType switch
        {
            _ when mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "image",
            _ when mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => "audio",
            _ when mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => "video",
            "application/pdf" => "document",
            _ => throw new NotSupportedException($"Unsupported Gemini media type: {mediaType}.")
        };
        var block = new JsonObject { ["type"] = type, ["mime_type"] = mediaType };
        if (content is DataContent inline)
        {
            block["data"] = inline.Base64Data.ToString();
        }
        else
        {
            block["uri"] = ((UriContent)content).Uri.AbsoluteUri;
        }

        return block;
    }

    private void AddOptions(JsonObject payload, ChatOptions? options)
    {
        options ??= new ChatOptions();
        var config = new JsonObject();

        if (ThinkingLevel is { Length: > 0 })
        {
            config["thinking_level"] = ThinkingLevel.ToLowerInvariant();
        }

        if (IncludeThoughts is { } shouldInclude)
        {
            config["thinking_summaries"] = shouldInclude ? "auto" : "none";
        }

        var temperature = options.Temperature ?? Temperature;
        if (temperature is { } temp) config["temperature"] = temp;

        var topP = options.TopP ?? TopP;
        if (topP is { } p) config["top_p"] = p;

        if (options.TopK is { } k) config["top_k"] = k;

        if (options.FrequencyPenalty is { } freq) config["frequency_penalty"] = freq;
        if (options.PresencePenalty is { } pres) config["presence_penalty"] = pres;
        if (options.Seed is { } seed) config["seed"] = seed;

        if (options.StopSequences is { Count: > 0 } stops)
        {
            JsonArray sequences = [.. stops];
            config["stop_sequences"] = sequences;
        }

        if (options.MaxOutputTokens is { } max) config["max_output_tokens"] = max;

        if (options.ResponseFormat is ChatResponseFormatJson json)
        {
            var format = new JsonObject { ["type"] = "text", ["mime_type"] = "application/json" };
            if (json.Schema is { } schema) format["schema"] = JsonObject.Create(schema);
            payload["response_format"] = format;
        }

        if (options.Reasoning?.Effort is { } effort)
        {
            config["thinking_level"] = effort switch
            {
                ReasoningEffort.Low => "low",
                ReasoningEffort.Medium => "medium",
                ReasoningEffort.High => "high",
                ReasoningEffort.ExtraHigh => "xhigh",
                _ => "medium"
            };
        }

        if (options.Reasoning?.Output is { } output)
        {
            config["thinking_summaries"] = output == ReasoningOutput.None ? "none" : "auto";
        }

        if (options.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in options.Tools)
            {
                if (tool is not AIFunctionDeclaration declaration)
                {
                    throw new NotSupportedException($"Unsupported Gemini tool declaration: {tool.GetType().Name}.");
                }

                tools.Add(
                    new JsonObject
                    {
                        ["type"] = "function",
                        ["name"] = declaration.Name,
                        ["description"] = declaration.Description,
                        ["parameters"] = JsonObject.Create(declaration.JsonSchema)
                    });
            }

            payload["tools"] = tools;
        }

        if (options.ToolMode is NoneChatToolMode)
        {
            config["tool_choice"] = "none";
        }
        else if (options.ToolMode is RequiredChatToolMode required)
        {
            config["tool_choice"] = required.RequiredFunctionName is { } name
                ? new JsonObject
                {
                    ["allowed_tools"] = new JsonObject
                    {
                        ["mode"] = "any",
                        ["tools"] = new JsonArray(JsonValue.Create(name))
                    }
                }
                : JsonValue.Create("any");
        }

        if (config.Count > 0) payload["generation_config"] = config;
    }

    private static ChatFinishReason? GetFinishReason(JsonElement interaction)
    {
        return TryGetString(interaction, "status") switch
        {
            "completed" => ChatFinishReason.Stop,
            "requires_action" => ChatFinishReason.ToolCalls,
            "incomplete" => new ChatFinishReason("incomplete"),
            "failed" or "cancelled" or "canceled" => new ChatFinishReason("aborted"),
            "requires_continuation" => new ChatFinishReason("pause_turn"),
            var status => throw new JsonException($"Expected a terminal Gemini interaction, received {status}.")
        };
    }

    private static UsageDetails? ReadUsage(JsonElement interaction)
    {
        if (!interaction.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        var output = GetCount("total_output_tokens");
        var thoughts = GetCount("total_thought_tokens");
        return new UsageDetails
        {
            InputTokenCount = GetCount("total_input_tokens"),
            OutputTokenCount = output.HasValue || thoughts.HasValue ? output.GetValueOrDefault() + thoughts.GetValueOrDefault() : null,
            ReasoningTokenCount = thoughts,
            TotalTokenCount = GetCount("total_tokens"),
            CachedInputTokenCount = GetCount("total_cached_tokens")
        };

        long? GetCount(string key)
        {
            return usage.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
        }
    }

    private static List<AIContent> ProjectStep(JsonElement step)
    {
        var contents = new List<AIContent>();
        switch (TryGetString(step, "type"))
        {
            case "model_output":
            {
                if (step.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in blocks.EnumerateArray())
                    {
                        contents.Add(DecodeContent(block));
                    }
                }
                break;
            }
            case "thought":
            {
                var signature = TryGetString(step, "signature");
                if (step.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in summary.EnumerateArray())
                    {
                        if (TryGetString(block, "type") == "text")
                        {
                            contents.Add(new TextReasoningContent(TryGetString(block, "text")));
                        }
                    }
                }

                if (contents.Count == 0)
                {
                    contents.Add(new TextReasoningContent(null));
                }

                ((TextReasoningContent)contents[^1]).ProtectedData = signature;
                break;
            }
            case "function_call":
            {
                var callId = TryGetString(step, "id") ?? TryGetString(step, "call_id");
                var name = TryGetString(step, "name");
                var arguments = step.TryGetProperty("arguments", out var argsElement) ? argsElement : default;
                contents.Add(ParseCall(callId, name, arguments));
                break;
            }
            case "google_search_call" or "google_search_result":
            {
                contents.Add(
                    new TextReasoningContent(null)
                    {
                        AdditionalProperties = new AdditionalPropertiesDictionary
                        {
                            [ReasoningMetadata.GeminiStepKey] = step.Clone()
                        }
                    });
                break;
            }
        }

        return contents;
    }

    private static FunctionCallContent ParseCall(string? id, string? name, JsonElement arguments)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new JsonException("A Gemini function call requires a name.");
        }

        var resolvedId = string.IsNullOrEmpty(id) ? $"call_{Guid.NewGuid():N}" : id;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in arguments.EnumerateObject())
            {
                values.Add(property.Name, property.Value.Clone());
            }
        }

        return new FunctionCallContent(resolvedId, name, values)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ReasoningMetadata.DialectKey] = DialectName
            }
        };
    }

    private static AIContent DecodeContent(JsonElement block)
    {
        if (TryGetString(block, "type") == "text")
        {
            return new TextContent(TryGetString(block, "text"));
        }

        var mime = TryGetString(block, "mime_type") ?? throw new JsonException("Missing Gemini media type.");
        if (block.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
        {
            return new DataContent(data.GetBytesFromBase64(), mime);
        }

        if (TryGetString(block, "uri") is { } uri)
        {
            return new UriContent(new Uri(uri), mime);
        }

        throw new JsonException("Missing Gemini media data or URI.");
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
