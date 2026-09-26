namespace Everywhere.AI;

/// <summary>
/// Represents the result of resolving reasoning effort values, including the list of available values and the effective value.
/// </summary>
/// <param name="Values"></param>
/// <param name="EffectiveValue"></param>
public readonly record struct ReasoningEffortResolution(
    IReadOnlyList<string> Values,
    string? EffectiveValue
);

/// <summary>
/// Provides methods to resolve reasoning effort values based on user input, selected value, and default values.
/// </summary>
public static class ReasoningEffortResolver
{
    /// <summary>
    /// Resolves the effective reasoning effort value based on user input, selected value, and default values.
    /// </summary>
    /// <param name="userValues"></param>
    /// <param name="selectedValue"></param>
    /// <param name="defaultValues"></param>
    /// <returns></returns>
    public static ReasoningEffortResolution Resolve(string? userValues, string? selectedValue, IReadOnlyList<string>? defaultValues)
    {
        var parsedUserValues = userValues?.Split(['|', '｜'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var values = parsedUserValues is { Length: > 0 } ? parsedUserValues : defaultValues ?? [];
        var effectiveValue = selectedValue is not null && values.Contains(selectedValue, StringComparer.Ordinal) ?
            selectedValue :
            values.Count == 0 ?
                null :
                values[values.Count / 2];
        return new ReasoningEffortResolution(values, effectiveValue);
    }
}