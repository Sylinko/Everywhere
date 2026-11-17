using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Everywhere.Harness.Providers.Gemini;

partial class GeminiGenerateContentClient
{
    private static void EncodeConversation(JsonObject payload, IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var systemParts = new JsonArray();
        if (options?.Instructions is { Length: > 0 } instruction)
        {
            systemParts.Add(new JsonObject { ["text"] = instruction });
        }

        var contents = new JsonArray();
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var portableCalls = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System || message.Role.Value == "developer")
            {
                if (message.Text.Length > 0)
                {
                    systemParts.Add(new JsonObject { ["text"] = message.Text });
                }

                continue;
            }

            var isAssistant = message.Role == ChatRole.Assistant;
            if (!isAssistant && message.Role != ChatRole.User && message.Role != ChatRole.Tool)
            {
                throw new NotSupportedException($"Unsupported Gemini input role: {message.Role.Value}.");
            }

            var hasNativeGeminiOrigin = isAssistant && message.Contents.Any(static part =>
                part is TextReasoningContent { ProtectedData.Length: > 0 } && IsCompatibleDialect(part.AdditionalProperties) ||
                part.AdditionalProperties?.TryGetValue(ReasoningMetadata.ThoughtSignatureKey, out var sig) is true &&
                sig is string { Length: > 0 } &&
                IsCompatibleDialect(part.AdditionalProperties) ||
                part.AdditionalProperties?.TryGetValue(ReasoningMetadata.DialectKey, out var d) is true &&
                string.Equals(d as string, DialectName, StringComparison.Ordinal));

            foreach (var part in message.Contents)
            {
                switch (part)
                {
                    case TextReasoningContent reasoning:
                    {
                        var hasSummary = !string.IsNullOrEmpty(reasoning.Text);
                        var hasSignature = !string.IsNullOrEmpty(reasoning.ProtectedData);
                        if (!hasSummary && !hasSignature) break;

                        // Do not replay unsigned thoughts from foreign providers as native Gemini thinking parts.
                        if (!hasSignature && !hasNativeGeminiOrigin) break;

                        var thoughtPart = new JsonObject
                        {
                            ["text"] = reasoning.Text ?? string.Empty,
                            ["thought"] = true
                        };
                        if (hasSignature)
                        {
                            thoughtPart["thoughtSignature"] = reasoning.ProtectedData;
                        }

                        AppendPart(contents, "model", thoughtPart);
                        break;
                    }
                    case FunctionCallContent call:
                    {
                        if (!isAssistant)
                        {
                            throw new InvalidDataException("Gemini function calls must originate from the model.");
                        }

                        var callSignature = call.AdditionalProperties is not null &&
                            call.AdditionalProperties.TryGetValue(ReasoningMetadata.ThoughtSignatureKey, out var rawSig) &&
                            rawSig is string { Length: > 0 } sigValue ?
                                sigValue :
                                null;
                        var hasExplicitGeminiDialect = call.AdditionalProperties is not null &&
                            call.AdditionalProperties.TryGetValue(ReasoningMetadata.DialectKey, out var rawDialect) &&
                            string.Equals(rawDialect as string, DialectName, StringComparison.Ordinal);
                        var isGeminiOrigin = hasNativeGeminiOrigin || hasExplicitGeminiDialect || callSignature is not null;

                        if (!isGeminiOrigin)
                        {
                            // Foreign or unsigned tool calls lack Gemini thought signatures; degrade to portable evidence
                            // so Gemini 3 thinking models do not reject the turn with missing_thought_signature.
                            portableCalls[call.CallId] = call.Name;
                            callNames.Remove(call.CallId);
                            var callEvidence = new JsonObject
                            {
                                ["prior_tool_call"] = call.Name,
                                ["id"] = call.CallId,
                                ["arguments"] = JsonSerializer.SerializeToNode(call.Arguments) ?? new JsonObject()
                            };
                            AppendPart(contents, "model", new JsonObject { ["text"] = callEvidence.ToJsonString() });
                            break;
                        }

                        portableCalls.Remove(call.CallId);
                        callNames[call.CallId] = call.Name;

                        var functionCallObj = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["args"] = JsonSerializer.SerializeToNode(call.Arguments) ?? new JsonObject()
                        };
                        var callPart = new JsonObject
                        {
                            ["functionCall"] = functionCallObj
                        };
                        if (callSignature is not null)
                        {
                            callPart["thoughtSignature"] = callSignature;
                        }

                        AppendPart(contents, "model", callPart);
                        break;
                    }
                    case FunctionResultContent result:
                    {
                        if (message.Role != ChatRole.Tool)
                        {
                            throw new InvalidDataException("A Gemini function result must belong to a tool message.");
                        }

                        if (portableCalls.TryGetValue(result.CallId, out var priorName))
                        {
                            AddPortableResult(contents, result, priorName);
                            break;
                        }

                        if (!callNames.TryGetValue(result.CallId, out var name))
                        {
                            throw new InvalidDataException($"Unpaired Gemini function result '{result.CallId}'.");
                        }

                        var responsePart = new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"] = name,
                                ["response"] = new JsonObject
                                {
                                    ["content"] = EncodeResult(result.Result)
                                }
                            }
                        };
                        AppendPart(contents, "function", responsePart);
                        break;
                    }
                    default:
                    {
                        if (message.Role == ChatRole.Tool)
                        {
                            throw new NotSupportedException("A Gemini tool message must contain a function result.");
                        }

                        AppendPart(contents, isAssistant ? "model" : "user", EncodePart(part));
                        break;
                    }
                }
            }
        }

        if (systemParts.Count > 0)
        {
            payload["systemInstruction"] = new JsonObject { ["parts"] = systemParts };
        }

        payload["contents"] = contents;
    }

    private static void AppendPart(JsonArray contents, string role, JsonObject part)
    {
        if (contents.Count > 0 &&
            contents[^1] is JsonObject last &&
            string.Equals(last["role"]?.GetValue<string>(), role, StringComparison.Ordinal) &&
            last["parts"] is JsonArray parts)
        {
            parts.Add(part);
            return;
        }

        contents.Add(
            new JsonObject
            {
                ["role"] = role,
                ["parts"] = new JsonArray(part)
            });
    }

    private static bool IsCompatibleDialect(AdditionalPropertiesDictionary? properties) =>
        properties is null ||
        !properties.TryGetValue(ReasoningMetadata.DialectKey, out var dialect) ||
        dialect is null ||
        string.Equals(dialect as string, DialectName, StringComparison.Ordinal);

    private static void AddPortableResult(JsonArray contents, FunctionResultContent result, string priorName)
    {
        var evidence = new JsonObject
        {
            ["prior_tool_result"] = priorName,
            ["call_id"] = result.CallId
        };
        if (result.Result is not IReadOnlyList<AIContent> blocks)
        {
            evidence["result"] = EncodeResult(result.Result);
            AppendPart(contents, "user", new JsonObject { ["text"] = evidence.ToJsonString() });
            return;
        }

        if (result.Result is ModelToolContent { IsError: true })
        {
            evidence["is_error"] = true;
        }

        AppendPart(contents, "user", new JsonObject { ["text"] = evidence.ToJsonString() });
        foreach (var block in blocks)
        {
            AppendPart(contents, "user", EncodePart(block));
        }
    }

    private static JsonNode? EncodeResult(object? result) => result is not IReadOnlyList<AIContent> parts ?
        JsonSerializer.SerializeToNode(result) :
        JsonValue.Create(string.Join("\n", parts.OfType<TextContent>().Select(static part => part.Text)));

    private static JsonObject EncodePart(AIContent content) => content switch
    {
        TextContent text => new JsonObject { ["text"] = text.Text },
        DataContent inline => new JsonObject
        {
            ["inlineData"] = new JsonObject
            {
                ["mimeType"] = inline.MediaType,
                ["data"] = inline.Base64Data.ToString()
            }
        },
        UriContent uri => new JsonObject
        {
            ["fileData"] = new JsonObject
            {
                ["mimeType"] = uri.MediaType,
                ["fileUri"] = uri.Uri.AbsoluteUri
            }
        },
        _ => throw new NotSupportedException($"Unsupported Gemini content: {content.GetType().Name}.")
    };

    private void AddOptions(JsonObject payload, ChatOptions? options)
    {
        options ??= new ChatOptions();
        var config = new JsonObject();

        var temperature = options.Temperature ?? Temperature;
        if (temperature is { } temp) config["temperature"] = temp;

        var topP = options.TopP ?? TopP;
        if (topP is { } p) config["topP"] = p;

        var topK = options.TopK ?? TopK;
        if (topK is { } k) config["topK"] = k;

        if (options.Seed is { } seed) config["seed"] = seed;
        if (options.FrequencyPenalty is { } freq) config["frequencyPenalty"] = freq;
        if (options.PresencePenalty is { } pres) config["presencePenalty"] = pres;
        if (options.MaxOutputTokens is { } max) config["maxOutputTokens"] = max;

        if (options.StopSequences is { Count: > 0 } stops)
        {
            var sequences = new JsonArray();
            foreach (var stop in stops) sequences.Add(stop);
            config["stopSequences"] = sequences;
        }

        if (options.ResponseFormat is ChatResponseFormatJson json)
        {
            config["responseMimeType"] = "application/json";
            if (json.Schema is { } schema)
            {
                config["responseSchema"] = JsonObject.Create(TransformToOpenApi3Schema(schema));
            }
        }

        var resolvedThinkingLevel = default(string);
        if (options.Reasoning?.Effort is { } effort)
        {
            resolvedThinkingLevel = effort switch
            {
                ReasoningEffort.Low => "low",
                ReasoningEffort.Medium => "medium",
                ReasoningEffort.High => "high",
                ReasoningEffort.ExtraHigh => "high",
                _ => "medium"
            };
        }
        else if (ThinkingLevel is { Length: > 0 })
        {
            resolvedThinkingLevel = ThinkingLevel.ToLowerInvariant();
        }

        var resolvedIncludeThoughts = options.Reasoning?.Output is { } reasoningOutput ? reasoningOutput != ReasoningOutput.None : IncludeThoughts;

        var shouldEmitThinking = resolvedIncludeThoughts is true ||
            resolvedThinkingLevel is not null ||
            ThinkingBudget.HasValue ||
            options.Reasoning?.Output == ReasoningOutput.None;
        if (shouldEmitThinking)
        {
            var thinkingConfig = new JsonObject();
            if (resolvedIncludeThoughts is { } includeThoughts) thinkingConfig["includeThoughts"] = includeThoughts;
            if (ThinkingBudget is { } budget) thinkingConfig["thinkingBudget"] = budget;
            if (resolvedThinkingLevel is not null) thinkingConfig["thinkingLevel"] = resolvedThinkingLevel;
            config["thinkingConfig"] = thinkingConfig;
        }

        if (config.Count > 0)
        {
            payload["generationConfig"] = config;
        }

        if (options.Tools is { Count: > 0 })
        {
            var declarations = new JsonArray();
            foreach (var tool in options.Tools)
            {
                if (tool is not AIFunctionDeclaration declaration)
                {
                    throw new NotSupportedException($"Unsupported Gemini tool declaration: {tool.GetType().Name}.");
                }

                var entry = new JsonObject
                {
                    ["name"] = declaration.Name,
                    ["description"] = declaration.Description
                };
                if (declaration.JsonSchema.ValueKind is JsonValueKind.Object)
                {
                    var transformed = TransformToOpenApi3Schema(declaration.JsonSchema);
                    if (JsonObject.Create(transformed) is { } parameters)
                    {
                        entry["parameters"] = parameters;
                    }
                }

                declarations.Add(entry);
            }

            payload["tools"] = new JsonArray(
                new JsonObject
                {
                    ["functionDeclarations"] = declarations
                });
        }

        if (options.ToolMode is NoneChatToolMode)
        {
            payload["toolConfig"] = new JsonObject
            {
                ["functionCallingConfig"] = new JsonObject
                {
                    ["mode"] = "NONE"
                }
            };
        }
        else if (options.ToolMode is RequiredChatToolMode required)
        {
            var fnConfig = new JsonObject { ["mode"] = "ANY" };
            if (required.RequiredFunctionName is { Length: > 0 } requiredName)
            {
                fnConfig["allowedFunctionNames"] = new JsonArray(JsonValue.Create(requiredName));
            }

            payload["toolConfig"] = new JsonObject
            {
                ["functionCallingConfig"] = fnConfig
            };
        }
    }

    private static void ProjectCandidateParts(JsonElement candidate, List<AIContent> destination, ref bool hasFunctionCalls)
    {
        if (!candidate.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            var thoughtSignature = TryGetString(part, "thoughtSignature");
            var isThought = part.TryGetProperty("thought", out var thoughtProp) && thoughtProp.ValueKind == JsonValueKind.True;

            if (part.TryGetProperty("functionCall", out var functionCall) && functionCall.ValueKind == JsonValueKind.Object)
            {
                hasFunctionCalls = true;
                var id = TryGetString(functionCall, "id");
                if (string.IsNullOrEmpty(id)) id = $"call_{Guid.NewGuid():N}";
                var name = TryGetString(functionCall, "name");
                if (string.IsNullOrEmpty(name))
                {
                    throw new JsonException("A Gemini function call requires a name.");
                }

                var args = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (functionCall.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in argsElement.EnumerateObject())
                    {
                        args.Add(property.Name, property.Value.Clone());
                    }
                }

                var additional = new AdditionalPropertiesDictionary
                {
                    [ReasoningMetadata.DialectKey] = DialectName
                };
                if (!string.IsNullOrEmpty(thoughtSignature))
                {
                    additional[ReasoningMetadata.ThoughtSignatureKey] = thoughtSignature;
                }

                destination.Add(
                    new FunctionCallContent(id, name, args)
                    {
                        AdditionalProperties = additional
                    });
                continue;
            }

            if (isThought)
            {
                var text = TryGetString(part, "text");
                if (!string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(thoughtSignature))
                {
                    destination.Add(
                        new TextReasoningContent(text)
                        {
                            ProtectedData = thoughtSignature
                        });
                }

                continue;
            }

            if (part.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
            {
                destination.Add(new TextContent(textProp.GetString()));
            }
            else if (part.TryGetProperty("inlineData", out var inlineData) && inlineData.ValueKind == JsonValueKind.Object)
            {
                var mime = TryGetString(inlineData, "mimeType") ?? throw new JsonException("Missing Gemini media type.");
                if (!inlineData.TryGetProperty("data", out var dataProp) || dataProp.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException("Missing Gemini inline media data.");
                }

                destination.Add(new DataContent(dataProp.GetBytesFromBase64(), mime));
            }
            else if (part.TryGetProperty("fileData", out var fileData) && fileData.ValueKind == JsonValueKind.Object)
            {
                var mime = TryGetString(fileData, "mimeType") ?? throw new JsonException("Missing Gemini file media type.");
                var uriString = TryGetString(fileData, "fileUri") ?? throw new JsonException("Missing Gemini file URI.");
                destination.Add(new UriContent(new Uri(uriString), mime));
            }

            if (!string.IsNullOrEmpty(thoughtSignature))
            {
                // Gemini 2.5/3 can attach thoughtSignature to the trailing answer part when no separate thought part exists.
                destination.Add(
                    new TextReasoningContent(null)
                    {
                        ProtectedData = thoughtSignature
                    });
            }
        }
    }

    private static bool TryGetFinishReason(JsonElement candidate, bool hasFunctionCalls, out ChatFinishReason? finishReason)
    {
        if (candidate.TryGetProperty("finishReason", out var reasonProp) &&
            reasonProp.ValueKind == JsonValueKind.String &&
            reasonProp.GetString() is { Length: > 0 } rawReason)
        {
            finishReason = MapFinishReason(rawReason, hasFunctionCalls);
            return true;
        }

        finishReason = null;
        return false;
    }

    private static ChatFinishReason MapFinishReason(string reason, bool hasFunctionCalls) =>
        reason.ToUpperInvariant() switch
        {
            "STOP" => hasFunctionCalls ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            "MAX_TOKENS" => ChatFinishReason.Length,
            "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII" or "IMAGE_SAFETY" or "IMAGE_PROHIBITED_CONTENT" => ChatFinishReason.ContentFilter,
            _ => new ChatFinishReason(reason.ToLowerInvariant())
        };

    private static UsageDetails? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usageMetadata", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var candidates = Count("candidatesTokenCount");
        var thoughts = Count("thoughtsTokenCount");
        return new UsageDetails
        {
            InputTokenCount = Count("promptTokenCount"),
            OutputTokenCount = candidates.HasValue || thoughts.HasValue ? candidates.GetValueOrDefault() + thoughts.GetValueOrDefault() : null,
            ReasoningTokenCount = thoughts,
            TotalTokenCount = Count("totalTokenCount"),
            CachedInputTokenCount = Count("cachedContentTokenCount")
        };

        long? Count(string key) => usage.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
    }
}
