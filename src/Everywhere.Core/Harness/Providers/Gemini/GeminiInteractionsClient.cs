using System.Globalization;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Everywhere.Harness.Providers.Gemini;

/// <summary>
/// Stateless Gemini Interactions v1 <see cref="IChatClient"/> transport.
/// The model connection owns the <see cref="HttpClient"/>; the caller owns retries and tool execution.
/// </summary>
public sealed partial class GeminiInteractionsClient(
    HttpClient http,
    Uri interactionsUri,
    string model,
    string? apiKey
)
    : IChatClient
{
    public const string DialectName = "gemini-interactions";

    private readonly ChatClientMetadata _metadata = new(DialectName, interactionsUri, model);

    public string? ThinkingLevel { get; init; }

    public bool? IncludeThoughts { get; init; }

    public float? Temperature { get; init; }

    public float? TopP { get; init; }

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
        using var response = await HttpErrorEvidence.SendAsync(http, request, "Gemini Interactions", cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var interaction = document.RootElement;
        var result = new ChatResponse();
        var stepIndex = 0;
        if (interaction.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in steps.EnumerateArray())
            {
                if (step.TryGetProperty("type", out var stepType) && stepType.GetString() == "user_input") continue;
                var captured = step.Clone();
                var message = new ChatMessage(ChatRole.Assistant, ProjectStep(captured))
                {
                    MessageId = (stepIndex++).ToString(CultureInfo.InvariantCulture),
                    RawRepresentation = captured
                };
                result.Messages.Add(message);
            }
        }

        result.ResponseId = TryGetString(interaction, "id");
        result.ModelId = TryGetString(interaction, "model") ?? model;
        result.FinishReason = GetFinishReason(interaction);
        result.Usage = ReadUsage(interaction);
        return result;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(messages, options, isStreaming: true);
        using var response = await HttpErrorEvidence.SendAsync(http, request, "Gemini Interactions", cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var decoder = new GeminiStreamDecoder();
        await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            var data = item.Data.Trim();
            if (data.Length == 0 || data == "[DONE]" || string.Equals(item.EventType, "done", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            foreach (var update in decoder.Read(document.RootElement))
            {
                yield return update;
            }

            if (decoder.IsComplete) yield break;
        }

        throw new EndOfStreamException("The Gemini interaction stream ended without a terminal event.");
    }

    private HttpRequestMessage CreateRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool isStreaming)
    {
        var input = new JsonArray();
        var selected = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var instructions = new List<string>();
        if (options?.Instructions is { Length: > 0 } instruction) instructions.Add(instruction);

        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var portableCalls = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var message in selected)
        {
            if ((message.Role == ChatRole.System || message.Role.Value == "developer") && message.Text.Length > 0)
            {
                instructions.Add(message.Text);
            }
        }

        var payload = new JsonObject { ["model"] = options?.ModelId ?? model };
        if (instructions.Count > 0)
        {
            payload["system_instruction"] = string.Join("\n\n", instructions);
        }

        AddOptions(payload, options);

        foreach (var message in selected)
        {
            if (message.Role == ChatRole.System || message.Role.Value == "developer") continue;
            var isAssistant = message.Role == ChatRole.Assistant;
            if (!isAssistant && message.Role != ChatRole.User && message.Role != ChatRole.Tool)
            {
                throw new NotSupportedException($"Unsupported Gemini input role: {message.Role.Value}.");
            }

            JsonArray? content = null;
            var hasNativeGeminiSignature = isAssistant && message.Contents.AsValueEnumerable()
                .Any(static part =>
                    part is TextReasoningContent { ProtectedData.Length: > 0 } && IsCompatibleDialect(part.AdditionalProperties) ||
                    part.AdditionalProperties?.TryGetValue(ReasoningMetadata.DialectKey, out var d) is true &&
                    string.Equals(d as string, DialectName, StringComparison.Ordinal));

            foreach (var part in message.Contents)
            {
                switch (part)
                {
                    case TextReasoningContent reasoning:
                    {
                        content = null;
                        if (reasoning.AdditionalProperties?.TryGetValue(ReasoningMetadata.GeminiStepKey, out var rawStep) is true &&
                            rawStep is JsonElement stepElement)
                        {
                            input.Add(JsonObject.Create(stepElement));
                            break;
                        }

                        var hasSummary = !string.IsNullOrEmpty(reasoning.Text);
                        var hasSignature = !string.IsNullOrEmpty(reasoning.ProtectedData);
                        if (!hasSummary && !hasSignature) break;

                        // Do not replay unsigned thoughts from foreign providers as native Gemini steps.
                        if (!hasSignature && !hasNativeGeminiSignature) break;

                        var thought = new JsonObject { ["type"] = "thought" };
                        if (hasSummary)
                        {
                            thought["summary"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reasoning.Text });
                        }

                        if (hasSignature)
                        {
                            thought["signature"] = reasoning.ProtectedData;
                        }

                        input.Add(thought);
                        break;
                    }
                    case FunctionCallContent call:
                    {
                        if (!isAssistant) throw new InvalidDataException("Gemini function calls must originate from the model.");
                        content = null;

                        var isGeminiOrigin = hasNativeGeminiSignature ||
                            call.AdditionalProperties?.TryGetValue(ReasoningMetadata.DialectKey, out var callDialect) is true &&
                            string.Equals(callDialect as string, DialectName, StringComparison.Ordinal);

                        if (!isGeminiOrigin)
                        {
                            // Foreign or legacy tool calls lack Gemini thought signatures; send portable evidence
                            // so Gemini 3/4 thinking models do not reject the request with missing_thought_signature.
                            portableCalls[call.CallId] = call.Name;
                            callNames.Remove(call.CallId);
                            var callEvidence = new JsonObject
                            {
                                ["prior_tool_call"] = call.Name,
                                ["id"] = call.CallId,
                                ["arguments"] = JsonSerializer.SerializeToNode(call.Arguments) ?? new JsonObject()
                            };
                            input.Add(
                                new JsonObject
                                {
                                    ["type"] = "model_output",
                                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = callEvidence.ToJsonString() })
                                });
                            break;
                        }

                        portableCalls.Remove(call.CallId);
                        callNames[call.CallId] = call.Name;
                        input.Add(
                            new JsonObject
                            {
                                ["type"] = "function_call",
                                ["id"] = call.CallId,
                                ["name"] = call.Name,
                                ["arguments"] = JsonSerializer.SerializeToNode(call.Arguments) ?? new JsonObject()
                            });
                        break;
                    }
                    case FunctionResultContent result:
                    {
                        content = null;
                        if (message.Role != ChatRole.Tool)
                        {
                            throw new InvalidDataException("A Gemini function result must reference a preceding model call.");
                        }

                        if (portableCalls.TryGetValue(result.CallId, out var priorName))
                        {
                            AddPortableResult(input, result, priorName);
                            break;
                        }

                        if (!callNames.TryGetValue(result.CallId, out var name))
                        {
                            throw new InvalidDataException($"Unpaired Gemini function result '{result.CallId}'.");
                        }

                        var step = new JsonObject
                        {
                            ["type"] = "function_result",
                            ["call_id"] = result.CallId,
                            ["name"] = name,
                            ["result"] = EncodeResult(result.Result)
                        };
                        if (result.Exception is not null || result.Result is ModelToolContent { IsError: true })
                        {
                            step["is_error"] = true;
                        }

                        input.Add(step);
                        break;
                    }
                    default:
                    {
                        if (message.Role == ChatRole.Tool)
                        {
                            throw new NotSupportedException("A Gemini tool message must contain a function result.");
                        }

                        if (content is null)
                        {
                            content = [];
                            input.Add(
                                new JsonObject
                                {
                                    ["type"] = isAssistant ? "model_output" : "user_input",
                                    ["content"] = content
                                });
                        }

                        content.Add(EncodeContent(part));
                        break;
                    }
                }
            }
        }

        payload["input"] = input;
        payload["store"] = false;
        payload["stream"] = isStreaming;
        var request = new HttpRequestMessage(HttpMethod.Post, interactionsUri)
        {
            Content = JsonContent.Create(payload)
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Add("x-goog-api-key", apiKey);
        }

        return request;
    }

    private static bool IsCompatibleDialect(AdditionalPropertiesDictionary? properties) =>
        properties is null ||
        !properties.TryGetValue(ReasoningMetadata.DialectKey, out var dialect) ||
        dialect is null ||
        string.Equals(dialect as string, DialectName, StringComparison.Ordinal);

    private static void AddPortableResult(JsonArray input, FunctionResultContent result, string priorName)
    {
        var evidence = new JsonObject
        {
            ["prior_tool_result"] = priorName,
            ["call_id"] = result.CallId
        };
        if (result.Result is not IReadOnlyList<AIContent> blocks)
        {
            evidence["result"] = EncodeResult(result.Result);
            input.Add(
                new JsonObject
                {
                    ["type"] = "user_input",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = evidence.ToJsonString() })
                });
            return;
        }

        if (result.Result is ModelToolContent { IsError: true })
        {
            evidence["is_error"] = true;
        }

        var contentBlocks = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = evidence.ToJsonString() });
        foreach (var block in blocks)
        {
            contentBlocks.Add(EncodeContent(block));
        }

        input.Add(new JsonObject { ["type"] = "user_input", ["content"] = contentBlocks });
    }
}
