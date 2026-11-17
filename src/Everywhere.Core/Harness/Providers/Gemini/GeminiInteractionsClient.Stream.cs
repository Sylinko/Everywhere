using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Everywhere.Harness.Providers.Gemini;

partial class GeminiInteractionsClient
{
    private sealed class GeminiStreamDecoder
    {
        public bool IsComplete { get; private set; }

        private readonly Dictionary<int, StepDraft> _active = [];
        private readonly SortedDictionary<int, ChatResponseUpdate> _completed = [];
        private readonly HashSet<int> _seen = [];
        private string? _responseId;
        private string? _model;

        public IReadOnlyList<ChatResponseUpdate> Read(JsonElement frame)
        {
            var kind = TryGetString(frame, "event_type");
            var update = new ChatResponseUpdate { Role = ChatRole.Assistant, ResponseId = _responseId, ModelId = _model };
            switch (kind)
            {
                case "interaction.created":
                {
                    if (frame.TryGetProperty("interaction", out var created) && created.ValueKind == JsonValueKind.Object)
                    {
                        _responseId = TryGetString(created, "id") ?? _responseId;
                        _model = TryGetString(created, "model") ?? _model;
                    }

                    return [];
                }
                case "interaction.status_update" or "interaction.updated" or "done":
                {
                    return [];
                }
                case "step.start":
                {
                    var index = frame.GetProperty("index").GetInt32();
                    if (!_seen.Add(index)) throw new JsonException($"Duplicate Gemini step {index}.");

                    var initial = frame.GetProperty("step");
                    _active.Add(index, new StepDraft(initial));
                    var stepType = TryGetString(initial, "type");
                    if (stepType == "function_call")
                    {
                        update.MessageId = index.ToString(CultureInfo.InvariantCulture);
                        update.RawRepresentation = initial.Clone();
                        return [update];
                    }

                    if (stepType != "model_output" && !(stepType == "thought" &&
                            (initial.TryGetProperty("summary", out _) || initial.TryGetProperty("signature", out _))))
                    {
                        return [];
                    }

                    update.Contents = ProjectStep(initial);
                    if (update.Contents.Count == 0) return [];

                    update.MessageId = index.ToString(CultureInfo.InvariantCulture);
                    return [update];
                }
                case "step.delta":
                {
                    var deltaIndex = frame.GetProperty("index").GetInt32();
                    if (!_active.TryGetValue(deltaIndex, out var draft))
                    {
                        throw new JsonException("Delta outside an active Gemini step.");
                    }

                    update.MessageId = deltaIndex.ToString(CultureInfo.InvariantCulture);
                    if (draft.Append(frame.GetProperty("delta")) is not { } content) return [];

                    update.Contents.Add(content);
                    return [update];
                }
                case "step.stop":
                {
                    var stopIndex = frame.GetProperty("index").GetInt32();
                    if (!_active.Remove(stopIndex, out var completed))
                    {
                        throw new JsonException("Stop outside an active Gemini step.");
                    }

                    var step = completed.Seal();
                    update.MessageId = stopIndex.ToString(CultureInfo.InvariantCulture);
                    var stepType = TryGetString(step, "type");
                    if (stepType is "function_call" or "google_search_call" or "google_search_result")
                    {
                        update.Contents = ProjectStep(step);
                    }

                    update.AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        [ReasoningMetadata.BlockBoundaryKey] = true
                    };

                    // Hold out-of-order step completions until all preceding step indices have stopped
                    // so tool calls and native search steps always assemble in authoritative step order.
                    _completed.Add(stopIndex, update);
                    var minActiveIndex = int.MaxValue;
                    foreach (var activeIndex in _active.Keys)
                    {
                        if (activeIndex < minActiveIndex)
                        {
                            minActiveIndex = activeIndex;
                        }
                    }

                    var ready = new List<ChatResponseUpdate>();
                    while (_completed.Count > 0)
                    {
                        var first = _completed.First();
                        if (first.Key >= minActiveIndex)
                        {
                            break;
                        }

                        _completed.Remove(first.Key);
                        ready.Add(first.Value);
                    }

                    return ready;
                }
                case "interaction.completed":
                {
                    if (_active.Count > 0) throw new JsonException("Gemini completed with open steps.");
                    var terminal = frame.TryGetProperty("interaction", out var interaction) && interaction.ValueKind == JsonValueKind.Object
                        ? interaction
                        : frame;
                    update.ResponseId = TryGetString(terminal, "id") ?? _responseId;
                    update.FinishReason = GetFinishReason(terminal);
                    if (ReadUsage(terminal) is { } usage)
                    {
                        update.Contents.Add(new UsageContent(usage));
                    }

                    IsComplete = true;
                    return [update];
                }
                case "error":
                {
                    throw new InvalidDataException(
                        frame.TryGetProperty("error", out var error) && TryGetString(error, "message") is { } message
                            ? message
                            : "Gemini interaction stream failed.");
                }
                default:
                {
                    throw new NotSupportedException($"Unsupported Gemini stream event: {kind}.");
                }
            }
        }
    }

    private sealed class StepDraft(JsonElement start)
    {
        private readonly JsonObject _step = JsonObject.Create(start.Clone()) ?? throw new JsonException("Missing Gemini step.");
        private StringBuilder? _text;
        private StringBuilder? _arguments;
        private JsonArray? _textOwner;

        public AIContent? Append(JsonElement delta)
        {
            var type = TryGetString(delta, "type");
            switch (type)
            {
                case "thought_signature":
                {
                    var signature = TryGetString(delta, "signature");
                    _step["signature"] = signature;
                    return new TextReasoningContent(null) { ProtectedData = signature };
                }
                case "arguments_delta":
                {
                    if (TryGetString(delta, "arguments") is { Length: > 0 } argChunk)
                    {
                        (_arguments ??= new StringBuilder()).Append(argChunk);
                    }

                    return null;
                }
                case "thought_summary":
                {
                    if (delta.TryGetProperty("content", out var summaryContent) && summaryContent.ValueKind == JsonValueKind.Object)
                    {
                        return AppendContent(summaryContent, "summary");
                    }

                    if (TryGetString(delta, "text") is { } flatSummary)
                    {
                        using var synthesized = JsonDocument.Parse(
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = flatSummary
                            }.ToJsonString());
                        return AppendContent(synthesized.RootElement, "summary");
                    }

                    return null;
                }
                case "text" or "image" or "audio" or "video" or "document":
                {
                    return AppendContent(delta, "content");
                }
                default:
                {
                    if (type != _step["type"]?.GetValue<string>())
                    {
                        throw new NotSupportedException($"Unsupported Gemini step delta: {type}.");
                    }

                    foreach (var property in delta.EnumerateObject())
                    {
                        if (property.Name == "type") continue;
                        if (_step[property.Name] is JsonArray list && property.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var element in property.Value.EnumerateArray())
                            {
                                list.Add(JsonNode.Parse(element.GetRawText()));
                            }
                        }
                        else
                        {
                            _step[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                        }
                    }

                    return null;
                }
            }
        }

        public JsonElement Seal()
        {
            FlushText();
            if (_arguments is not null)
            {
                var rawArgs = _arguments.ToString();
                _step["arguments"] = string.IsNullOrWhiteSpace(rawArgs)
                    ? new JsonObject()
                    : JsonNode.Parse(rawArgs)?.AsObject()
                    ?? throw new JsonException("Missing function arguments.");
            }

            return JsonSerializer.SerializeToElement(_step);
        }

        private AIContent AppendContent(JsonElement content, string field)
        {
            var blocks = (_step[field] ??= new JsonArray()).AsArray();
            var projection = DecodeContent(content);
            if (projection is TextContent text && content.EnumerateObject().Count() == 2)
            {
                if (!ReferenceEquals(_textOwner, blocks)) FlushText();
                _textOwner = blocks;
                (_text ??= new StringBuilder()).Append(text.Text);
            }
            else
            {
                FlushText();
                blocks.Add(JsonObject.Create(content.Clone()));
            }

            return field == "summary" && projection is TextContent summary
                ? new TextReasoningContent(summary.Text)
                : projection;
        }

        private void FlushText()
        {
            if (_textOwner is null || _text is null) return;
            _textOwner.Add(new JsonObject { ["type"] = "text", ["text"] = _text.ToString() });
            _text = null;
            _textOwner = null;
        }
    }
}
