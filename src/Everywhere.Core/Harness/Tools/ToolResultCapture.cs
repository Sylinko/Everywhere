using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Chat;
using Everywhere.Prompting.Documents;
using Everywhere.Serialization;
using Everywhere.Storage;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace Everywhere.Harness;

/// <summary>
/// Converts a completed tool invocation result into durable application types before persistence.
/// </summary>
public static class ToolResultCapture
{
    /// <summary>
    /// Captures and normalizes a <see cref="ToolResultRecord"/>, storing any inline binary blobs.
    /// </summary>
    public static async ValueTask<ToolResultRecord> CaptureAsync(
        ToolResultRecord result,
        IBlobStorage storage,
        CancellationToken cancellationToken)
    {
        var files = default(List<ToolResultFile>);
        object? value;
        switch (result.Result)
        {
            case null or string or ChatFunctionResult or ChatAttachment or PromptNode or ToolContentResult:
            {
                return result;
            }
            case JsonElement json when json.ValueKind != JsonValueKind.Undefined:
            {
                value = json.Clone();
                break;
            }
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
            case JsonNode:
            {
                value = JsonSerializer.SerializeToElement(result.Result, result.Result.GetType());
                break;
            }
            case CallToolResult
            {
                Content: [TextContentBlock { Meta: null, Annotations: null } text],
                IsError: null,
                StructuredContent: null,
                Meta: null
            }:
            {
                value = text.Text;
                break;
            }
            case CallToolResult mcp:
            {
                var blocks = new List<ToolContentBlock>(mcp.Content.Count);
                foreach (var block in mcp.Content)
                {
                    blocks.Add(await CaptureMcpBlockAsync(block));
                }

                value = new ToolContentResult
                {
                    Blocks = blocks.ToArray(),
                    IsError = mcp.IsError,
                    StructuredContent = mcp.StructuredContent?.Clone(),
                    ProtocolMetadata = ToElement(mcp.Meta)
                };
                break;
            }
            case TextContent { AdditionalProperties: null, Annotations: null } text:
            {
                value = text.Text;
                break;
            }
            case AIContent content:
            {
                value = new ToolContentResult { Blocks = [await CaptureBlockAsync(content)] };
                break;
            }
            case IReadOnlyList<AIContent> contents:
            {
                var items = new ToolContentBlock[contents.Count];
                for (var i = 0; i < contents.Count; i++)
                {
                    items[i] = await CaptureBlockAsync(contents[i]);
                }

                value = new ToolContentResult { Blocks = items };
                break;
            }
            default:
            {
                // Structured POCO or dictionary returns (e.g. ask_user_question dictionary) serialize cleanly to JsonElement.
                value = JsonSerializer.SerializeToElement(result.Result, result.Result.GetType(), AIJsonUtilities.DefaultOptions);
                break;
            }
        }

        return result with { Result = value, Files = files?.ToArray() ?? [] };

        async ValueTask<ToolContentBlock> CaptureMcpBlockAsync(ContentBlock block)
        {
            var details = new JsonObject();
            if (block.Meta is not null) details["_meta"] = block.Meta.DeepClone();
            if (block.Annotations is not null) details["annotations"] = JsonSerializer.SerializeToNode(block.Annotations);

            ToolContentBlock captured;
            switch (block)
            {
                case TextContentBlock text:
                {
                    captured = new ToolContentBlock.Text(text.Text);
                    break;
                }
                case ImageContentBlock image:
                {
                    captured = await StoreFileAsync(image.DecodedData, image.MimeType);
                    break;
                }
                case AudioContentBlock audio:
                {
                    captured = await StoreFileAsync(audio.DecodedData, audio.MimeType);
                    break;
                }
                case EmbeddedResourceBlock { Resource: BlobResourceContents resource }:
                {
                    details["resourceUri"] = resource.Uri;
                    if (resource.Meta is not null) details["resourceMeta"] = resource.Meta.DeepClone();
                    captured = await StoreFileAsync(resource.DecodedData, resource.MimeType ?? "application/octet-stream");
                    break;
                }
                case ResourceLinkBlock link:
                {
                    captured = new ToolContentBlock.Uri(link.Uri, link.MimeType);
                    var linkDetails = JsonSerializer.SerializeToNode<ContentBlock>(link) as JsonObject
                        ?? throw new JsonException("Missing MCP resource link.");
                    linkDetails.Remove("uri");
                    linkDetails.Remove("mimeType");
                    return captured with { ProtocolDetails = ToElement(linkDetails) };
                }
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }:
                {
                    details["resourceUri"] = resource.Uri;
                    details["resourceMimeType"] = resource.MimeType;
                    if (resource.Meta is not null) details["resourceMeta"] = resource.Meta.DeepClone();
                    captured = new ToolContentBlock.Text(resource.Text);
                    break;
                }
                default:
                {
                    throw new NotSupportedException($"MCP block '{block.Type}' has no registered representation.");
                }
            }

            return details.Count == 0 ? captured : captured with { ProtocolDetails = ToElement(details) };
        }

        async ValueTask<ToolContentBlock> CaptureBlockAsync(AIContent block)
        {
            var properties = MetadataDictionaryMessagePackFormatter.CaptureProperties(block.AdditionalProperties);
            ToolContentBlock captured = block switch
            {
                TextContent text => new ToolContentBlock.Text(text.Text),
                UriContent uri => new ToolContentBlock.Uri(uri.Uri.OriginalString, uri.MediaType),
                DataContent data => await StoreFileAsync(data.Data, data.MediaType),
                _ => throw new NotSupportedException($"Tool content '{block.GetType().Name}' has no registered representation.")
            };
            var details = block is DataContent { Name: { } name } ? ToElement(new JsonObject { ["name"] = name }) : null;
            return captured with { Properties = properties, ProtocolDetails = details };
        }

        async ValueTask<ToolContentBlock.File> StoreFileAsync(ReadOnlyMemory<byte> data, string mediaType)
        {
            using var stream = MemoryMarshal.TryGetArray(data, out var segment) && segment.Array is { } buffer
                ? new MemoryStream(buffer, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(data.ToArray(), writable: false);
            var blob = await storage.StorageBlobAsync(stream, mediaType, cancellationToken: cancellationToken);
            files ??= [];
            var index = files.FindIndex(file => file.Sha256 == blob.Sha256 && file.MimeType == mediaType);
            if (index >= 0) return new ToolContentBlock.File(index);

            var file = new ToolResultFile(blob.Sha256, mediaType);
            file.Attachment.FilePath = blob.LocalPath;
            files.Add(file);
            return new ToolContentBlock.File(files.Count - 1);
        }
    }

    private static JsonElement? ToElement(JsonNode? node) =>
        node is null ? null : JsonSerializer.SerializeToElement(node);
}
