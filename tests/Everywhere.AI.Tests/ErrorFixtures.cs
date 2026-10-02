using System.Text.Json;
using System.Text.Json.Serialization;

namespace Everywhere.AI.Tests;

/// <summary>Wire data with provenance; later classification tests can reuse these cases.</summary>
public sealed class ErrorFixture
{
    public required string Id { get; init; }
    public required Provider[] Providers { get; init; }
    public required int Status { get; init; }
    public JsonElement? Body { get; init; }
    public string? RawBody { get; init; }
    public string? ContentType { get; init; }
    public required string Provenance { get; init; }
    public string? Source { get; init; }
    public string? ObservedAt { get; init; }
    public string? Notes { get; init; }

    public string WireBody => RawBody ?? Body?.GetRawText() ?? string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ErrorFixture[]))]
public partial class FixtureJsonContext : JsonSerializerContext;
