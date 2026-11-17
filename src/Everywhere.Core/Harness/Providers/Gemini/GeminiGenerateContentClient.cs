using System.Globalization;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.AI;
using Microsoft.Extensions.AI;

namespace Everywhere.Harness.Providers.Gemini;

/// <summary>
/// Stateless Google Gemini <c>generateContent</c> / <c>streamGenerateContent</c> <see cref="IChatClient"/> transport.
/// Supports Gemini <c>v1</c> and <c>v1beta</c> endpoints, OpenAPI 3.0 tool schemas, thinking configuration,
/// call-level and thought-level <c>thoughtSignature</c> continuation, and portable JSON degradation for foreign turns.
/// </summary>
public sealed partial class GeminiGenerateContentClient(
    HttpClient http,
    Uri baseUri,
    string model,
    string? apiKey
) : IChatClient
{
    public const string DialectName = "gemini";

    private readonly ChatClientMetadata _metadata = new(DialectName, baseUri, model);

    public string? ThinkingLevel { get; init; }

    public int? ThinkingBudget { get; init; }

    public bool? IncludeThoughts { get; init; }

    public float? Temperature { get; init; }

    public float? TopP { get; init; }

    public int? TopK { get; init; }

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
        CancellationToken cancellationToken = default
    )
    {
        var effectiveModel = options?.ModelId ?? model;
        using var request = CreateRequest(messages, options, isStreaming: false);
        using var response = await HttpErrorEvidence.SendAsync(http, request, "Gemini", cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var captured = root.Clone();

        var contents = new List<AIContent>();
        var hasFunctionCalls = false;
        var finishReason = default(ChatFinishReason?);

        if (TryGetFirstCandidate(root, out var candidate))
        {
            ProjectCandidateParts(candidate, contents, ref hasFunctionCalls);
            if (TryGetFinishReason(candidate, hasFunctionCalls, out var parsedReason))
            {
                finishReason = parsedReason;
            }
        }
        else if (TryGetPromptBlockReason(root, out var blockReason))
        {
            finishReason = MapFinishReason(blockReason, hasFunctionCalls);
        }

        var message = new ChatMessage(ChatRole.Assistant, contents)
        {
            RawRepresentation = captured
        };

        return new ChatResponse(message)
        {
            ResponseId = TryGetString(root, "responseId"),
            ModelId = TryGetString(root, "modelVersion") ?? effectiveModel,
            FinishReason = finishReason,
            Usage = ReadUsage(root),
            RawRepresentation = captured
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var effectiveModel = options?.ModelId ?? model;
        using var request = CreateRequest(messages, options, isStreaming: true);
        using var response = await HttpErrorEvidence.SendAsync(http, request, "Gemini", cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);

        var hasFunctionCalls = false;
        var hasTerminalEvent = false;
        var hasEmittedUsage = false;
        var latestUsage = default(UsageDetails);
        var responseId = default(string);
        var responseModel = effectiveModel;
        var chunkIndex = 0;

        await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            var data = item.Data.Trim();
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            var chunk = document.RootElement;
            responseId = TryGetString(chunk, "responseId") ?? responseId;
            responseModel = TryGetString(chunk, "modelVersion") ?? responseModel;
            latestUsage = ReadUsage(chunk) ?? latestUsage;

            var contents = new List<AIContent>();
            var finishReason = default(ChatFinishReason?);

            if (TryGetFirstCandidate(chunk, out var candidate))
            {
                ProjectCandidateParts(candidate, contents, ref hasFunctionCalls);
                if (TryGetFinishReason(candidate, hasFunctionCalls, out var candidateFinishReason))
                {
                    finishReason = candidateFinishReason;
                    hasTerminalEvent = true;
                }
            }
            else if (TryGetPromptBlockReason(chunk, out var blockReason))
            {
                finishReason = MapFinishReason(blockReason, hasFunctionCalls);
                hasTerminalEvent = true;
            }

            if (hasTerminalEvent && latestUsage is not null && !hasEmittedUsage)
            {
                // Gemini SSE repeats cumulative usage counts on chunks; emit once on termination so MEAI does not sum them.
                contents.Add(new UsageContent(latestUsage));
                hasEmittedUsage = true;
            }

            if (contents.Count > 0 || finishReason is not null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, contents)
                {
                    ResponseId = responseId,
                    MessageId = (chunkIndex++).ToString(CultureInfo.InvariantCulture),
                    ModelId = responseModel,
                    FinishReason = finishReason,
                    RawRepresentation = chunk.Clone()
                };
            }
        }

        if (!hasTerminalEvent)
        {
            throw new EndOfStreamException("The Gemini generateContent stream ended without a terminal finishReason.");
        }
    }

    private HttpRequestMessage CreateRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool isStreaming)
    {
        var selected = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var effectiveModel = options?.ModelId ?? model;
        var targetUri = ModelProviderSchemaExtensions.GetGeminiGenerateContentUri(baseUri, effectiveModel, isStreaming);

        var payload = new JsonObject();
        EncodeConversation(payload, selected, options);
        AddOptions(payload, options);

        var request = new HttpRequestMessage(HttpMethod.Post, targetUri)
        {
            Content = JsonContent.Create(payload)
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Add("x-goog-api-key", apiKey);
        }

        return request;
    }

    private static bool TryGetFirstCandidate(JsonElement root, out JsonElement candidate)
    {
        if (root.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array &&
            candidates.GetArrayLength() > 0)
        {
            candidate = candidates[0];
            return candidate.ValueKind == JsonValueKind.Object;
        }

        candidate = default;
        return false;
    }

    private static bool TryGetPromptBlockReason(JsonElement root, out string blockReason)
    {
        if (root.TryGetProperty("promptFeedback", out var feedback) &&
            feedback.ValueKind == JsonValueKind.Object &&
            feedback.TryGetProperty("blockReason", out var reasonProp) &&
            reasonProp.ValueKind == JsonValueKind.String &&
            reasonProp.GetString() is { Length: > 0 } value)
        {
            blockReason = value;
            return true;
        }

        blockReason = string.Empty;
        return false;
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
}
