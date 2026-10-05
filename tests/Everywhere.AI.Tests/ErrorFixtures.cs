using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.Common;

namespace Everywhere.AI.Tests;

/// <summary>Wire data with provenance; later classification tests can reuse these cases.</summary>
public sealed class ErrorFixture
{
    public required string Id { get; init; }
    public required Provider[] Providers { get; init; }
    public required int Status { get; init; }
    public required string ExpectedType { get; init; }
    public required string ExpectedRecovery { get; init; }
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
[JsonSerializable(typeof(ObservedErrorFixture[]))]
public partial class FixtureJsonContext : JsonSerializerContext;

/// <summary>Sanitized Sentry messages; structured HTTP response evidence and original SDK types were not exported.</summary>
public sealed class ObservedErrorFixture
{
    public required string ShortId { get; init; }
    public required string ErrorMessage { get; init; }
    public required string ExpectedType { get; init; }
    public required string Provenance { get; init; }
    public required string Notes { get; init; }
}
