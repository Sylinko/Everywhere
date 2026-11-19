using System.Text;
using Everywhere.Chat;
using Microsoft.Extensions.AI;

namespace Everywhere.Harness;

/// <summary>
/// Consumes MEAI <see cref="ChatResponseUpdate"/> deltas for one model attempt, assembling
/// ordered text/reasoning blocks and completed client <see cref="FunctionCallContent"/> items.
/// </summary>
public sealed class ResponseAssembler(
    string? dialect = null,
    Func<bool, ResponseAssembler.Block, ChatTextBuffer>? openVisibleBuffer = null,
    Action<ResponseAssembler.Block>? onBlockUpdated = null
)
{
    public IReadOnlyList<Block> Blocks => _blocks;

    public IReadOnlyList<FunctionCallContent> FunctionCalls => _functionCalls;

    private readonly List<Block> _blocks = [];
    private readonly List<FunctionCallContent> _functionCalls = [];
    private Block? _current;
    private string? _currentSegmentId;

    /// <summary>
    /// Appends all content items from a single streaming <see cref="ChatResponseUpdate"/>.
    /// </summary>
    public void AppendUpdate(ChatResponseUpdate update)
    {
        foreach (var item in update.Contents)
        {
            AppendItem(item);
        }

        ApplyBoundary(update);
    }

    /// <summary>
    /// Appends a single <see cref="AIContent"/> item from a streaming update.
    /// </summary>
    public void AppendItem(AIContent item)
    {
        switch (item)
        {
            case TextReasoningContent reasoning:
            {
                var itemId = reasoning.AdditionalProperties?.TryGetValue("reasoningItemId", out var rawId) is true ? rawId as string : null;
                var isRedacted = reasoning.AdditionalProperties?.TryGetValue(ReasoningMetadata.IsRedactedKey, out var rawRedacted) is true && rawRedacted is true;
                var metadata = GetProviderReasoningMetadata(reasoning, itemId, isRedacted);

                // An empty companion delta from a Chat Completions endpoint is not a change of block.
                if (string.IsNullOrEmpty(reasoning.Text) && metadata is null) break;

                // Start a fresh reasoning block when the native item ID changes, or when a redacted/native step arrives.
                if (_current is { IsReasoning: true } &&
                    (isRedacted || metadata?.ContainsKey(ReasoningMetadata.GeminiStepKey) is true ||
                        _current.Metadata.ContainsKey(ReasoningMetadata.IsRedactedKey) ||
                        _current.Metadata.ContainsKey(ReasoningMetadata.GeminiStepKey) ||
                        itemId is not null && _currentSegmentId is not null && itemId != _currentSegmentId))
                {
                    EndBlock();
                }

                if (!string.IsNullOrEmpty(dialect))
                {
                    (metadata ??= [])[ReasoningMetadata.DialectKey] = dialect;
                }

                Append(true, reasoning.Text, metadata, itemId);
                break;
            }
            case TextContent { Text.Length: > 0 } text:
            {
                Append(false, text.Text, null);
                break;
            }
            case FunctionCallContent { InformationalOnly: false } call:
            {
                EndBlock();
                _functionCalls.Add(call);
                break;
            }
        }
    }

    /// <summary>
    /// Closes the current block when the update carries an explicit provider block-stop boundary.
    /// </summary>
    public void ApplyBoundary(ChatResponseUpdate update)
    {
        if (update.AdditionalProperties?.TryGetValue(ReasoningMetadata.BlockBoundaryKey, out var boundary) is true && boundary is true)
        {
            EndBlock();
        }
    }

    /// <summary>
    /// Appends a single text or reasoning delta directly.
    /// </summary>
    public Block Append(
        bool isReasoning,
        string? text,
        IReadOnlyDictionary<string, object?>? metadata,
        string? segmentId = null)
    {
        if (_current is null || _current.IsReasoning != isReasoning)
        {
            _current = new Block(isReasoning);
            _blocks.Add(_current);
            _currentSegmentId = segmentId;
        }
        else if (segmentId is not null)
        {
            _currentSegmentId ??= segmentId;
        }

        _current.Append(text, metadata, openVisibleBuffer);
        onBlockUpdated?.Invoke(_current);
        return _current;
    }

    /// <summary>
    /// Ends adjacency for the active block so the next delta opens a distinct block.
    /// </summary>
    public void EndBlock()
    {
        _current = null;
        _currentSegmentId = null;
    }

    /// <summary>
    /// Resets all accumulated blocks and tool calls for a fresh attempt.
    /// </summary>
    public void Reset()
    {
        _blocks.Clear();
        _functionCalls.Clear();
        _current = null;
        _currentSegmentId = null;
    }

    /// <summary>
    /// Concatenates all non-reasoning text blocks into a single string.
    /// </summary>
    public string GetAnswerText()
    {
        var first = default(string);
        var joined = default(StringBuilder);
        foreach (var block in _blocks)
        {
            if (block.IsReasoning || block.Text is null)
            {
                continue;
            }

            var text = block.Text.ToString();
            if (first is null)
            {
                first = text;
            }
            else
            {
                joined ??= new StringBuilder(first);
                joined.Append(text);
            }
        }

        return joined?.ToString() ?? first ?? string.Empty;
    }

    private static Dictionary<string, object?>? GetProviderReasoningMetadata(TextReasoningContent reasoning, string? itemId, bool isRedacted)
    {
        Dictionary<string, object?>? metadata = null;

        if (!string.IsNullOrEmpty(reasoning.ProtectedData))
        {
            (metadata ??= [])[ReasoningMetadata.ProtectedDataKey] = reasoning.ProtectedData;
        }

        if (!string.IsNullOrEmpty(itemId))
        {
            (metadata ??= [])[ReasoningMetadata.ItemIdKey] = itemId;
        }

        if (isRedacted)
        {
            (metadata ??= [])[ReasoningMetadata.IsRedactedKey] = true;
        }

        if (reasoning.AdditionalProperties?.TryGetValue(ReasoningMetadata.GeminiStepKey, out var geminiStep) is true && geminiStep is not null)
        {
            (metadata ??= [])[ReasoningMetadata.GeminiStepKey] = geminiStep;
        }

        return metadata;
    }

    /// <summary>
    /// Represents a contiguous answer or reasoning block assembled during one attempt.
    /// </summary>
    public sealed class Block(bool isReasoning)
    {
        public bool IsReasoning { get; } = isReasoning;

        public ChatTextBuffer? Text { get; private set; }

        public MetadataDictionary Metadata { get; private set; }

        /// <summary>
        /// Gets or sets the host span bound to this block when used by visible chat consumers.
        /// </summary>
        public AssistantChatMessageSpan? BoundSpan { get; set; }

        public void Append(
            string? text,
            IReadOnlyDictionary<string, object?>? metadata,
            Func<bool, Block, ChatTextBuffer>? openVisibleBuffer)
        {
            if (metadata is not null)
            {
                foreach (var (key, value) in metadata)
                {
                    Metadata = Metadata.SetItem(key, value);
                }
            }

            if (!string.IsNullOrEmpty(text))
            {
                Text ??= openVisibleBuffer?.Invoke(IsReasoning, this) ?? new ChatTextBuffer();
                Text.Append(text);
            }
        }
    }
}