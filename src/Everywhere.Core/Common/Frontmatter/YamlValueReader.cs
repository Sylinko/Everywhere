using System.Globalization;

namespace Everywhere.Common.Frontmatter;

internal static class YamlValueReader
{
    public static string? ReadString(
        IReadOnlyDictionary<string, object?> values,
        string key,
        List<FrontmatterDiagnostic> diagnostics)
    {
        if (!values.TryGetValue(key, out var value)) return null;
        if (value is string text) return text;

        diagnostics.Add(CreateInvalidFieldDiagnostic(key, "string"));
        return null;
    }

    public static bool? ReadBool(
        IReadOnlyDictionary<string, object?> values,
        string key,
        List<FrontmatterDiagnostic> diagnostics)
    {
        if (!values.TryGetValue(key, out var value)) return null;
        if (value is bool boolean) return boolean;

        diagnostics.Add(CreateInvalidFieldDiagnostic(key, "bool"));
        return null;
    }

    public static int? ReadInt(
        IReadOnlyDictionary<string, object?> values,
        string key,
        List<FrontmatterDiagnostic> diagnostics)
    {
        if (!values.TryGetValue(key, out var value)) return null;
        if (value is int intValue) return intValue;
        if (value is long longValue and >= int.MinValue and <= int.MaxValue) return (int)longValue;

        diagnostics.Add(CreateInvalidFieldDiagnostic(key, "int"));
        return null;
    }

    public static IReadOnlyDictionary<string, object?>? ReadMap(
        IReadOnlyDictionary<string, object?> values,
        string key,
        List<FrontmatterDiagnostic> diagnostics)
    {
        if (!values.TryGetValue(key, out var value)) return null;
        if (value is IReadOnlyDictionary<string, object?> map) return map;

        diagnostics.Add(CreateInvalidFieldDiagnostic(key, "mapping"));
        return null;
    }

    public static IReadOnlyList<string>? ReadStringList(
        IReadOnlyDictionary<string, object?> values,
        string key,
        List<FrontmatterDiagnostic> diagnostics)
    {
        if (!values.TryGetValue(key, out var value)) return null;
        if (value is not IReadOnlyList<object?> list)
        {
            diagnostics.Add(CreateInvalidFieldDiagnostic(key, "string array"));
            return null;
        }

        var result = new List<string>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is string text)
            {
                result.Add(text);
                continue;
            }

            diagnostics.Add(CreateInvalidFieldDiagnostic($"{key}[{i}]", "string"));
        }

        return result;
    }

    public static string? ReadDurationString(
        IReadOnlyDictionary<string, object?> values,
        string key,
        List<FrontmatterDiagnostic> diagnostics)
    {
        var value = ReadString(values, key, diagnostics);
        if (value is null) return null;

        if (TryParseDuration(value, out _)) return value;

        diagnostics.Add(new FrontmatterDiagnostic(
            "frontmatter.invalid_duration",
            $"Field '{key}' has an invalid duration.",
            key));
        return value;
    }

    public static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var trimmed = value.Trim();
        if (trimmed.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseNumber(trimmed[..^2], out duration, TimeSpan.FromMilliseconds);
        }

        if (trimmed.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseNumber(trimmed[..^1], out duration, TimeSpan.FromSeconds);
        }

        return false;
    }

    public static IReadOnlyDictionary<string, string> FlattenScalarMetadata(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlySet<string> excludedKeys)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            if (excludedKeys.Contains(key)) continue;

            AddFlattened(result, key, value);
        }

        return result;
    }

    private static void AddFlattened(Dictionary<string, string> result, string path, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case string text when !string.IsNullOrWhiteSpace(text):
                result[path] = text.Trim();
                return;
            case bool boolean:
                result[path] = boolean ? "true" : "false";
                return;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                result[path] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                return;
            case IReadOnlyDictionary<string, object?> map:
            {
                foreach (var (key, childValue) in map)
                {
                    AddFlattened(result, $"{path}.{key}", childValue);
                }

                return;
            }
        }
    }

    private static bool TryParseNumber(string value, out TimeSpan duration, Func<double, TimeSpan> factory)
    {
        duration = TimeSpan.Zero;
        if (!double.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number < 0)
        {
            return false;
        }

        duration = factory(number);
        return true;
    }

    private static FrontmatterDiagnostic CreateInvalidFieldDiagnostic(string path, string expectedType) =>
        new("frontmatter.invalid_field", $"Field '{path}' must be {expectedType}.", path);
}
