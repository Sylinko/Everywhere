using System.Text.Json;
using System.Text.Json.Serialization;

namespace Everywhere.AI.Tests;

/// <summary>
/// Owns only this test's expectations and request log on a shared MockServer instance.
/// Provisioning belongs to the opt-in runner, not ordinary test discovery.
/// </summary>
public sealed class MockServerSession : IAsyncDisposable
{
    public Uri Endpoint { get; }

    private readonly HttpClient _control;
    private readonly string _scope = Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private object RequestMatcher => new { method = "POST", headers = new Dictionary<string, string[]> { ["x-everywhere-test"] = [_scope] } };

    private MockServerSession(Uri endpoint)
    {
        Endpoint = endpoint;
        _control = new HttpClient { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(10) };
    }

    public static async Task<MockServerSession> ConnectAsync()
    {
        var address = Environment.GetEnvironmentVariable("EVERYWHERE_MOCKSERVER_URL");
        if (string.IsNullOrWhiteSpace(address))
        {
            if (Environment.GetEnvironmentVariable("EVERYWHERE_REQUIRE_MOCKSERVER") == "1")
                Assert.Fail("EVERYWHERE_MOCKSERVER_URL is required for this integration run.");
            Assert.Ignore("Set EVERYWHERE_MOCKSERVER_URL or use tools/test_llm.ps1 to run real HTTP SDK tests.");
        }

        var session = new MockServerSession(new Uri(address, UriKind.Absolute));
        try
        {
            using var response = await session._control.PutAsync("/mockserver/status", null);
            response.EnsureSuccessStatusCode();
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.That(status.RootElement.GetProperty("version").GetString(), Is.EqualTo("8.0.0"), "MockServer wire behavior is version-pinned.");
            return session;
        }
        catch
        {
            session._control.Dispose();
            throw;
        }
    }

    public HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var client = new HttpClient(handler ?? new SocketsHttpHandler()) { BaseAddress = Endpoint, Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("x-everywhere-test", _scope);
        return client;
    }

    public Task RespondAsync(object response, int? times = null) => RegisterAsync("httpResponse", response, times);

    public Task StreamAsync(object response) => RegisterAsync("httpSseResponse", response);

    public Task FailTransportAsync(object error) => RegisterAsync("httpError", error);

    public Task CompleteAsync(Provider provider, object completion, object? chaos = null, int? times = null)
    {
        if (provider != Provider.GeminiInteractions)
            return RegisterAsync("httpLlmResponse", new { provider = provider == Provider.OpenAIResponses ? "OPENAI_RESPONSES" : provider.ToString().ToUpperInvariant(), model = "test-model", completion, chaos }, times);

        // MockServer 8's GEMINI action speaks generateContent, not Interactions.
        var fixture = JsonSerializer.SerializeToElement(completion);
        var frames = new List<object>();
        var index = 0;
        if (fixture.TryGetProperty("text", out var text))
            AddStep(new { type = "model_output", content = new[] { new { type = "text", text } } });
        if (fixture.TryGetProperty("toolCalls", out var calls))
        {
            foreach (var call in calls.EnumerateArray())
                AddStep(new { type = "function_call", id = call.GetProperty("id"), name = call.GetProperty("name"),
                    arguments = JsonSerializer.Deserialize<JsonElement>(call.GetProperty("arguments").GetString()!) });
        }
        var status = fixture.TryGetProperty("stopReason", out var reason) ? reason.GetString() switch
        {
            "length" => "incomplete", "tool_calls" => "requires_action", _ => "completed"
        } : "completed";
        var usage = fixture.TryGetProperty("usage", out var tokens) ? new
        {
            total_input_tokens = tokens.GetProperty("inputTokens"), total_output_tokens = tokens.GetProperty("outputTokens")
        } : null;
        frames.Add(new { event_type = "interaction.completed", interaction = new { id = "int-test", status, usage } });
        var body = string.Concat(frames.Select(frame => "data: " + JsonSerializer.Serialize(frame, SerializerOptions) + "\n\n"));
        return RespondAsync(new { statusCode = 200, headers = new Dictionary<string, string[]> { ["Content-Type"] = ["text/event-stream"] }, body }, times);

        void AddStep(object step)
        {
            frames.Add(new { event_type = "step.start", index, step });
            frames.Add(new { event_type = "step.stop", index });
            index++;
        }
    }

    private async Task RegisterAsync(string action, object value, int? times = null)
    {
        var expectation = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString("N"), ["httpRequest"] = RequestMatcher, [action] = value
        };
        if (times is { } count) expectation["times"] = new { remainingTimes = count, unlimited = false };
        using var response = await _control.PutAsJsonAsync("/mockserver/expectation", expectation, SerializerOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(response.IsSuccessStatusCode, Is.True, body);
    }

    public async Task<JsonElement[]> RequestsAsync()
    {
        using var response = await _control.PutAsJsonAsync("/mockserver/retrieve?type=REQUESTS", RequestMatcher);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement[]>() ?? [];
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var response = await _control.PutAsJsonAsync("/mockserver/clear?type=ALL", RequestMatcher);
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            _control.Dispose();
        }
    }
}
