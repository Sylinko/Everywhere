using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Everywhere.Harness.Providers.Mistral;

/// <summary>
/// Stateless Mistral AI <see cref="IChatClient"/> over HTTP and SSE, supporting
/// Mistral's structured thinking content arrays, reasoning effort, tool calls, and usage reporting.
/// </summary>
public sealed class MistralChatClient : IChatClient
{
    public const string DialectName = "mistral";

    private readonly HttpClient _http;
    private readonly Uri _completionsUri;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly ChatClientMetadata _metadata;

    public float? Temperature { get; init; }

    public float? TopP { get; init; }

    public string? ReasoningEffort { get; init; }

    public MistralChatClient(HttpClient http, Uri baseEndpoint, string model, string? apiKey)
    {
        _http = http;
        var baseText = baseEndpoint.AbsoluteUri.TrimEnd('/');
        _completionsUri = baseText.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ?
            baseEndpoint :
            new Uri($"{baseText}/chat/completions");
        _model = model;
        _apiKey = apiKey;
        _metadata = new ChatClientMetadata(DialectName, _completionsUri, model);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is not null) return null;
        return serviceType == typeof(ChatClientMetadata) ? _metadata : serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose() { }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(messages, options, isStreaming: false);
        using var response = await HttpErrorEvidence.SendAsync(_http, request, "Mistral AI", cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var result = new ChatResponse
        {
            ResponseId = root.TryGetProperty("id", out var id) ? id.GetString() : null,
            ModelId = root.TryGetProperty("model", out var modelProp) ? modelProp.GetString() : _model,
            Usage = ReadUsage(root)
        };

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            result.FinishReason = ParseFinishReason(choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null);
            if (choice.TryGetProperty("message", out var msg))
            {
                var contents = new List<AIContent>();
                var (text, reasoning) = ExtractTextAndReasoning(msg);
                if (!string.IsNullOrEmpty(reasoning))
                {
                    contents.Add(new TextReasoningContent(reasoning));
                }

                if (!string.IsNullOrEmpty(text))
                {
                    contents.Add(new TextContent(text));
                }

                if (msg.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        var callId = tc.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                        var func = tc.TryGetProperty("function", out var fnProp) ? fnProp : default;
                        var name = func.ValueKind == JsonValueKind.Object && func.TryGetProperty("name", out var nameProp) ?
                            nameProp.GetString() :
                            null;
                        var argsJson = func.ValueKind == JsonValueKind.Object && func.TryGetProperty("arguments", out var argsProp) ?
                            argsProp.ValueKind == JsonValueKind.String ? argsProp.GetString() : argsProp.GetRawText() :
                            null;
                        contents.Add(BuildFunctionCall(callId ?? $"call_{index}", name ?? string.Empty, argsJson));
                        index++;
                    }
                }

                result.Messages.Add(new ChatMessage(ChatRole.Assistant, contents));
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(messages, options, isStreaming: true);
        using var response = await HttpErrorEvidence.SendAsync(_http, request, "Mistral AI", cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);

        var toolCallDrafts = new List<ToolCallDraft>();
        var hasEmittedToolCalls = false;
        var responseId = default(string);
        var modelId = _model;
        var finishReason = default(ChatFinishReason?);

        await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            var data = item.Data.Trim();
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            var chunk = document.RootElement;
            if (chunk.TryGetProperty("id", out var idProp) && idProp.GetString() is { } id)
            {
                responseId = id;
            }

            if (chunk.TryGetProperty("model", out var modelProp) && modelProp.GetString() is { } modelValue)
            {
                modelId = modelValue;
            }

            var updateContents = new List<AIContent>();
            var hasToolDelta = false;

            if (chunk.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var frProp) && frProp.ValueKind == JsonValueKind.String)
                {
                    finishReason = ParseFinishReason(frProp.GetString()) ?? finishReason;
                }

                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    var (text, reasoning) = ExtractTextAndReasoning(delta);
                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        updateContents.Add(new TextReasoningContent(reasoning));
                    }

                    if (!string.IsNullOrEmpty(text))
                    {
                        updateContents.Add(new TextContent(text));
                    }

                    if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        hasToolDelta = MergeToolCallDeltas(toolCallDrafts, toolCalls);
                    }
                }
            }

            if (finishReason is not null && !hasEmittedToolCalls && toolCallDrafts.Count > 0)
            {
                hasEmittedToolCalls = true;
                for (var i = 0; i < toolCallDrafts.Count; i++)
                {
                    var draft = toolCallDrafts[i];
                    updateContents.Add(BuildFunctionCall(draft.Id ?? $"call_{i}", draft.Name ?? string.Empty, draft.Arguments.ToString()));
                }
            }

            if (ReadUsage(chunk) is { } usage)
            {
                updateContents.Add(new UsageContent(usage));
            }

            if (updateContents.Count > 0 || finishReason is not null || hasToolDelta)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, updateContents)
                {
                    ResponseId = responseId,
                    ModelId = modelId,
                    FinishReason = finishReason,
                    RawRepresentation = hasToolDelta ? ToolDeltaMarker.Instance : null
                };
            }
        }

        if (!hasEmittedToolCalls && toolCallDrafts.Count > 0)
        {
            var remaining = new List<AIContent>(toolCallDrafts.Count);
            for (var i = 0; i < toolCallDrafts.Count; i++)
            {
                var draft = toolCallDrafts[i];
                remaining.Add(BuildFunctionCall(draft.Id ?? $"call_{i}", draft.Name ?? string.Empty, draft.Arguments.ToString()));
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, remaining)
            {
                ResponseId = responseId,
                ModelId = modelId,
                FinishReason = finishReason ?? ChatFinishReason.ToolCalls
            };
        }
    }

    public sealed class ToolDeltaMarker
    {
        public static ToolDeltaMarker Instance { get; } = new();
    }

    private HttpRequestMessage CreateRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool isStreaming)
    {
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var wireMessages = new JsonArray();

        if (!string.IsNullOrEmpty(options?.Instructions))
        {
            wireMessages.Add(
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = options.Instructions
                });
        }

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Assistant)
            {
                var chunks = new JsonArray();
                var plainText = new StringBuilder();
                var hasThinking = false;
                JsonArray? toolCalls = null;

                foreach (var item in message.Contents)
                {
                    switch (item)
                    {
                        case TextReasoningContent { Text.Length: > 0 } reasoning:
                        {
                            hasThinking = true;
                            chunks.Add(
                                new JsonObject
                                {
                                    ["type"] = "thinking",
                                    ["thinking"] = new JsonArray(
                                        new JsonObject
                                        {
                                            ["type"] = "text",
                                            ["text"] = reasoning.Text
                                        })
                                });
                            break;
                        }
                        case TextContent { Text.Length: > 0 } text:
                        {
                            plainText.Append(text.Text);
                            chunks.Add(
                                new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = text.Text
                                });
                            break;
                        }
                        case FunctionCallContent call:
                        {
                            callNames[call.CallId] = call.Name;
                            toolCalls ??= [];
                            toolCalls.Add(
                                new JsonObject
                                {
                                    ["id"] = call.CallId,
                                    ["type"] = "function",
                                    ["function"] = new JsonObject
                                    {
                                        ["name"] = call.Name,
                                        ["arguments"] = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>())
                                    }
                                });
                            break;
                        }
                    }
                }

                var assistantObj = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = hasThinking ? chunks : JsonValue.Create(plainText.ToString())
                };
                if (toolCalls is { Count: > 0 })
                {
                    assistantObj["tool_calls"] = toolCalls;
                }

                wireMessages.Add(assistantObj);
                continue;
            }

            if (message.Role == ChatRole.Tool)
            {
                foreach (var result in message.Contents.OfType<FunctionResultContent>())
                {
                    var textResult = result.Result switch
                    {
                        null => string.Empty,
                        string s => s,
                        _ => JsonSerializer.Serialize(result.Result)
                    };
                    var toolObj = new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = result.CallId,
                        ["content"] = textResult
                    };
                    if (callNames.TryGetValue(result.CallId, out var fnName))
                    {
                        toolObj["name"] = fnName;
                    }

                    wireMessages.Add(toolObj);
                }

                continue;
            }

            var role = message.Role == ChatRole.System || message.Role.Value == "developer" ? "system" : "user";
            if (message.Contents is [TextContent singleText])
            {
                wireMessages.Add(
                    new JsonObject
                    {
                        ["role"] = role,
                        ["content"] = singleText.Text
                    });
                continue;
            }

            var userChunks = new JsonArray();
            foreach (var item in message.Contents)
            {
                switch (item)
                {
                    case TextContent { Text.Length: > 0 } text:
                    {
                        userChunks.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                        break;
                    }
                    case DataContent data when data.HasTopLevelMediaType("image"):
                    {
                        userChunks.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = data.Uri });
                        break;
                    }
                    case UriContent uri when uri.HasTopLevelMediaType("image"):
                    {
                        userChunks.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = uri.Uri.AbsoluteUri });
                        break;
                    }
                    case UriContent docUri:
                    {
                        userChunks.Add(new JsonObject { ["type"] = "document_url", ["document_url"] = docUri.Uri.AbsoluteUri });
                        break;
                    }
                }
            }

            wireMessages.Add(
                new JsonObject
                {
                    ["role"] = role,
                    ["content"] = userChunks.Count > 0 ? userChunks : JsonValue.Create(string.Empty)
                });
        }

        var payload = new JsonObject
        {
            ["model"] = options?.ModelId ?? _model,
            ["messages"] = wireMessages,
            ["stream"] = isStreaming
        };

        var temperature = options?.Temperature ?? Temperature;
        if (temperature is { } temp) payload["temperature"] = temp;

        var topP = options?.TopP ?? TopP;
        if (topP is { } p) payload["top_p"] = p;

        if (options?.MaxOutputTokens is { } maxTokens) payload["max_tokens"] = maxTokens;
        if (options?.Seed is { } seed) payload["random_seed"] = seed;
        if (options?.FrequencyPenalty is { } freq) payload["frequency_penalty"] = freq;
        if (options?.PresencePenalty is { } pres) payload["presence_penalty"] = pres;
        if (options?.StopSequences is { Count: > 0 } stops)
        {
            var stopArr = new JsonArray();
            foreach (var stop in stops) stopArr.Add(stop);
            payload["stop"] = stopArr;
        }

        if (!string.IsNullOrWhiteSpace(ReasoningEffort))
        {
            payload["reasoning_effort"] = ReasoningEffort.Trim();
        }

        if (options is { Tools: { Count: > 0 } tools })
        {
            var toolArr = new JsonArray();
            foreach (var tool in tools)
            {
                if (tool is not AIFunctionDeclaration declaration) continue;
                toolArr.Add(
                    new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = declaration.Name,
                            ["description"] = declaration.Description,
                            ["parameters"] = JsonObject.Create(declaration.JsonSchema)
                        }
                    });
            }

            if (toolArr.Count > 0)
            {
                payload["tools"] = toolArr;
                payload["tool_choice"] = options.ToolMode switch
                {
                    NoneChatToolMode => JsonValue.Create("none"),
                    RequiredChatToolMode { RequiredFunctionName: { } requiredName } => new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = requiredName }
                    },
                    RequiredChatToolMode => JsonValue.Create("any"),
                    _ => JsonValue.Create("auto")
                };
            }
        }
        else if (options?.ToolMode is NoneChatToolMode)
        {
            payload["tool_choice"] = "none";
        }

        var requestMessage = new HttpRequestMessage(HttpMethod.Post, _completionsUri)
        {
            Content = JsonContent.Create(payload)
        };
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(isStreaming ? "text/event-stream" : "application/json"));

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return requestMessage;
    }

    private static (string? Text, string? Reasoning) ExtractTextAndReasoning(JsonElement element)
    {
        var explicitReasoning = element.TryGetProperty("reasoning_content", out var rcProp) && rcProp.ValueKind == JsonValueKind.String ?
            rcProp.GetString() :
            null;

        if (!element.TryGetProperty("content", out var content))
        {
            return (null, explicitReasoning);
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return (content.GetString(), explicitReasoning);
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return (null, explicitReasoning);
        }

        StringBuilder? text = null;
        StringBuilder? reasoning = null;
        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            switch (typeProp.GetString())
            {
                case "text" when item.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String:
                {
                    (text ??= new StringBuilder()).Append(textProp.GetString());
                    break;
                }
                case "thinking" when item.TryGetProperty("thinking", out var thinkingArr) && thinkingArr.ValueKind == JsonValueKind.Array:
                {
                    foreach (var part in thinkingArr.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var partType) &&
                            partType.GetString() == "text" &&
                            part.TryGetProperty("text", out var partText) &&
                            partText.ValueKind == JsonValueKind.String)
                        {
                            (reasoning ??= new StringBuilder()).Append(partText.GetString());
                        }
                    }
                    break;
                }
            }
        }

        var combinedReasoning = reasoning?.ToString();
        return (text?.ToString(), string.IsNullOrEmpty(combinedReasoning) ? explicitReasoning : combinedReasoning);
    }

    private static bool MergeToolCallDeltas(List<ToolCallDraft> accumulated, JsonElement updates)
    {
        var hasUpdates = false;
        var pos = 0;
        foreach (var update in updates.EnumerateArray())
        {
            hasUpdates = true;
            var id = update.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null;
            int? index = update.TryGetProperty("index", out var idxProp) && idxProp.TryGetInt32(out var idxVal) ? idxVal : null;

            ToolCallDraft? target = null;
            if (!string.IsNullOrEmpty(id))
            {
                target = accumulated.FirstOrDefault(item => item.Id == id);
            }

            if (target is null && index is { } callIndex)
            {
                target = accumulated.FirstOrDefault(item => item.Index == callIndex);
            }

            if (target is null && index is null && pos < accumulated.Count && string.IsNullOrEmpty(id))
            {
                target = accumulated[pos];
            }

            if (target is null)
            {
                target = new ToolCallDraft { Index = index };
                accumulated.Add(target);
            }

            target.Id ??= id;
            target.Index ??= index;

            if (update.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            {
                if (fn.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { Length: > 0 } name)
                {
                    target.Name ??= name;
                }

                if (fn.TryGetProperty("arguments", out var argsProp))
                {
                    var fragment = argsProp.ValueKind == JsonValueKind.String ? argsProp.GetString() : argsProp.GetRawText();
                    if (!string.IsNullOrEmpty(fragment))
                    {
                        target.Arguments.Append(fragment);
                    }
                }
            }

            pos++;
        }

        return hasUpdates;
    }

    private static FunctionCallContent BuildFunctionCall(string callId, string name, string? argumentsJson)
    {
        Dictionary<string, object?>? arguments = null;
        Exception? exception = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        arguments[prop.Name] = prop.Value.Clone();
                    }
                }
            }
            catch (JsonException ex)
            {
                exception = new JsonException("Function call arguments were invalid JSON.", ex);
            }
        }

        return new FunctionCallContent(callId, name, arguments)
        {
            Exception = exception,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ReasoningMetadata.DialectKey] = DialectName
            }
        };
    }

    private static UsageDetails? ReadUsage(JsonElement element)
    {
        if (!element.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        long? Count(string key) => usage.TryGetProperty(key, out var prop) && prop.TryGetInt64(out var val) ? val : null;
        var input = Count("prompt_tokens");
        var output = Count("completion_tokens");
        var total = Count("total_tokens");
        if (!input.HasValue && !output.HasValue && !total.HasValue) return null;

        return new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            TotalTokenCount = total
        };
    }

    private static ChatFinishReason? ParseFinishReason(string? reason) => reason switch
    {
        null or "" => null,
        "stop" => ChatFinishReason.Stop,
        "length" or "model_length" => ChatFinishReason.Length,
        "tool_calls" => ChatFinishReason.ToolCalls,
        "content_filter" => ChatFinishReason.ContentFilter,
        _ => new ChatFinishReason(reason)
    };

    private sealed class ToolCallDraft
    {
        public string? Id { get; set; }
        public int? Index { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}