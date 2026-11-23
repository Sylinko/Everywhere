using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Everywhere.Core.Tests.Harness;

internal sealed class McpTestServer : HttpMessageHandler
{
    public const string Image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=";

    public static JsonElement Schema { get; } = JsonSerializer.Deserialize<JsonElement>(
        """{"type":"object","properties":{"query":{"$ref":"#/$defs/term"}},"required":["query"],"additionalProperties":false,"$defs":{"term":{"type":"string"}}}""");

    public List<(string Server, string Name, string Argument)> Calls { get; } = [];

    public ConcurrentQueue<(string Server, string Method)> Requests { get; } = new();

    public Func<string, JsonElement, CancellationToken, Task<HttpResponseMessage>>? ToolListOverride { get; set; }

    public Func<string, JsonElement, JsonElement, CancellationToken, Task<HttpResponseMessage>>? ToolCallOverride { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post)
        {
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var message = doc.RootElement;
        if (!message.TryGetProperty("id", out var id))
        {
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        var server = request.RequestUri!.Host.Split('.')[0];
        var method = message.GetProperty("method").GetString() ?? throw new AssertionException("MCP method is missing.");
        Requests.Enqueue((server, method));

        if (method == "tools/list" && ToolListOverride is { } listTools)
        {
            return await listTools(server, id.Clone(), cancellationToken);
        }

        if (method == "tools/call" && ToolCallOverride is { } callTool)
        {
            return await callTool(server, id.Clone(), message.GetProperty("params").Clone(), cancellationToken);
        }

        var result = method switch
        {
            "server/discover" => new
            {
                supportedVersions = new[] { "2026-07-28" },
                capabilities = new { tools = new { } },
                ttlMs = 0,
                cacheScope = "private"
            },
            "tools/list" => new { tools = new[] { new { name = "lookup.file", description = server, inputSchema = Schema } } },
            "tools/call" => Call(server, message.GetProperty("params")),
            _ => throw new AssertionException($"Unexpected MCP method: {method}")
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }), Encoding.UTF8, "application/json")
        };
    }

    public object Call(string server, JsonElement args)
    {
        var name = args.GetProperty("name").GetString() ?? throw new AssertionException("MCP tool name is missing.");
        var query = args.GetProperty("arguments").GetProperty("query").GetString() ?? throw new AssertionException("MCP query is missing.");
        lock (Calls)
        {
            Calls.Add((server, name, query));
        }

        return new
        {
            content = new object[]
            {
                new { type = "text", text = "found rose" },
                new { type = "image", mimeType = "image/png", data = Image },
                new { type = "text", text = "after image" }
            },
            isError = true,
            structuredContent = new { score = 42 },
            _meta = new { trace = "private-marker" }
        };
    }
}
