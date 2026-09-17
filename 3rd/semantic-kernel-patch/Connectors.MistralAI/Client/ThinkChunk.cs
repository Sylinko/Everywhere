// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json.Serialization;

namespace Microsoft.SemanticKernel.Connectors.MistralAI.Client;

/// <summary>
/// Represents a Mistral thinking content chunk.
/// </summary>
internal sealed class ThinkChunk(string thinking) : ContentChunk(new ContentChunkType("thinking"))
{
    [JsonPropertyName("thinking")]
    public IList<TextChunk> Thinking { get; set; } = [new(thinking)];
}