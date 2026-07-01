using System.Text.Json;
using System.Text.Json.Serialization;

namespace Everywhere.Serialization;

/// <summary>
/// Converter for DateTimeOffset that serializes to ISO 8601 string format.
/// </summary>
public sealed class JsonISOStringDateTimeOffsetFormatter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected string token, got {reader.TokenType}.");
        }

        var dateString = reader.GetString();
        if (dateString == null)
        {
            throw new JsonException("Date string cannot be null.");
        }

        if (!DateTimeOffset.TryParse(dateString, null, System.Globalization.DateTimeStyles.RoundtripKind, out var date))
        {
            throw new JsonException($"Invalid date format: {dateString}");
        }

        return date;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString("o")); // "o" for ISO 8601 format
    }
}