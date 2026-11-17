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
            var mime when mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "image",
            var mime when mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => "audio",
            var mime when mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => "video",
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

        if (ThinkingLevel is { Length: > 0 } level)
        {
            config["thinking_level"] = level.ToLowerInvariant();
        }

        if (ThinkingBudget is { } budget)
        {
            config["thinking_budget"] = budget;
        }

        if (IncludeThoughts is { } shouldInclude)
        {
            config["thinking_summaries"] = shouldInclude ? "auto" : "none";
        }

        var temperature = options.Temperature ?? Temperature;
        if (temperature is { } temp) config["temperature"] = temp;

        var topP = options.TopP ?? TopP;
        if (topP is { } p) config["top_p"] = p;

        var topK = options.TopK ?? TopK;
        if (topK is { } k) config["top_k"] = k;

        if (options.FrequencyPenalty is { } freq) config["frequency_penalty"] = freq;
        if (options.PresencePenalty is { } pres) config["presence_penalty"] = pres;
        if (options.Seed is { } seed) config["seed"] = seed;

        if (options.StopSequences is { Count: > 0 } stops)
        {
            var sequences = new JsonArray();
            foreach (var stop in stops) sequences.Add(stop);
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

                tools.Add(new JsonObject
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
        return interaction.GetProperty("status").GetString() switch
        {
            "completed" => ChatFinishReason.Stop,
            "requires_action" => ChatFinishReason.ToolCalls,
            "incomplete" => new ChatFinishReason("incomplete"),
            "failed" or "cancelled" => new ChatFinishReason("aborted"),
            "requires_continuation" => new ChatFinishReason("pause_turn"),
            var status => throw new JsonException($"Expected a terminal Gemini interaction, received {status}.")
        };
    }

    private static UsageDetails? ReadUsage(JsonElement interaction)
    {
        if (!interaction.TryGetProperty("usage", out var usage)) return null;
        var output = Count("total_output_tokens");
        var thoughts = Count("total_thought_tokens");
        return new UsageDetails
        {
            InputTokenCount = Count("total_input_tokens"),
            OutputTokenCount = output.HasValue || thoughts.HasValue ? output.GetValueOrDefault() + thoughts.GetValueOrDefault() : null,
            ReasoningTokenCount = thoughts,
            TotalTokenCount = Count("total_tokens"),
            CachedInputTokenCount = Count("total_cached_tokens")
        };

        long? Count(string key) => usage.TryGetProperty(key, out var value) ? value.GetInt64() : null;
    }

    private static List<AIContent> ProjectStep(JsonElement step)
    {
        var contents = new List<AIContent>();
        switch (step.GetProperty("type").GetString())
        {
            case "model_output":
            {
                if (step.TryGetProperty("content", out var blocks))
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
                var signature = step.TryGetProperty("signature", out var value) ? value.GetString() : null;
                if (step.TryGetProperty("summary", out var summary))
                {
                    foreach (var block in summary.EnumerateArray())
                    {
                        if (block.GetProperty("type").GetString() == "text")
                        {
                            contents.Add(new TextReasoningContent(block.GetProperty("text").GetString()));
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
                contents.Add(ParseCall(
                    step.GetProperty("id").GetString(),
                    step.GetProperty("name").GetString(),
                    step.GetProperty("arguments")));
                break;
            }
            case "google_search_call" or "google_search_result":
            {
                contents.Add(new TextReasoningContent(null)
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
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
        {
            throw new JsonException("A Gemini function call requires an ID and name.");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            values.Add(property.Name, property.Value.Clone());
        }

        return new FunctionCallContent(id, name, values)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ReasoningMetadata.DialectKey] = DialectName
            }
        };
    }

    private static AIContent DecodeContent(JsonElement block)
    {
        if (block.GetProperty("type").GetString() == "text")
        {
            return new TextContent(block.GetProperty("text").GetString());
        }

        var mime = block.GetProperty("mime_type").GetString() ?? throw new JsonException("Missing Gemini media type.");
        if (block.TryGetProperty("data", out var data))
        {
            return new DataContent(data.GetBytesFromBase64(), mime);
        }

        if (block.TryGetProperty("uri", out var uri))
        {
            return new UriContent(new Uri(uri.GetString() ?? throw new JsonException("Missing media URI.")), mime);
        }

        throw new JsonException("Missing Gemini media data or URI.");
    }
}
