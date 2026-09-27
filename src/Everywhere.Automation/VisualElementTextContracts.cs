namespace Everywhere.Automation;

/// <summary>
/// Identifies the confidence of a visual text stream's reported total length.
/// </summary>
public enum VisualTextLengthKind
{
    /// <summary>The reported value is the exact UTF-16 length.</summary>
    Exact,

    /// <summary>The reported value is a proven UTF-16 lower bound.</summary>
    LowerBound,
}

/// <summary>
/// Defines bounded native-text observation limits shared by structural previews and explicit text reads.
/// </summary>
public static class VisualTextReadLimits
{
    /// <summary>Gets the maximum UTF-16 prefix inspected for one structural preview.</summary>
    public const int MaximumPreviewProbeCharacters = 1_048_576;

    /// <summary>Gets the maximum UTF-16 text inspected across one explicit text-read operation.</summary>
    /// <remarks>This corresponds to a 20 MiB UTF-16 payload before provider and managed-object overhead.</remarks>
    public const int MaximumTextReadProbeCharacters = 20 * 1_024 * 1_024 / sizeof(char);
}

/// <summary>
/// Describes the total length of a visual text stream together with the confidence of that value.
/// </summary>
public readonly record struct VisualTextLength
{
    /// <summary>Gets the confidence of this measurement.</summary>
    public VisualTextLengthKind Kind { get; }

    /// <summary>Gets the nonnegative measured value.</summary>
    public int Value { get; }

    private VisualTextLength(VisualTextLengthKind kind, int value)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Kind = kind;
        Value = value;
    }

    /// <summary>Creates an exact UTF-16 length.</summary>
    public static VisualTextLength Exact(int value) => new(VisualTextLengthKind.Exact, value);

    /// <summary>Creates a proven UTF-16 lower bound.</summary>
    public static VisualTextLength LowerBound(int value) => new(VisualTextLengthKind.LowerBound, value);

    /// <summary>Reconstructs a validated text-length measurement from a serialized contract.</summary>
    public static VisualTextLength Create(VisualTextLengthKind kind, int value) => new(kind, value);
}

/// <summary>
/// Contains one best-effort page of visual-element text and its next numeric offset.
/// </summary>
/// <param name="Text">The observed page, or <see langword="null" /> when no textual capability was available.</param>
/// <param name="NextOffset">The next UTF-16 offset, or <see langword="null" /> when this observation found no more text.</param>
/// <param name="Failure">The normalized provider failure, if one occurred.</param>
/// <param name="TotalLength">The best available measurement of the complete logical text stream.</param>
/// <param name="Offset">The resolved nonnegative UTF-16 offset of <paramref name="Text" />.</param>
/// <param name="ProbedLength">The number of source UTF-16 code units materialized or inspected for this observation.</param>
/// <param name="IsEndRelativeOffsetBounded">Whether a negative requested offset was resolved against a bounded observed prefix because the actual end was not reached.</param>
/// <remarks>
/// The offset is stateless and does not make a changing visual tree immutable. Concurrent content changes may cause overlap or omission between pages.
/// </remarks>
public sealed record VisualElementTextReadResult(
    string? Text,
    int? NextOffset,
    VisualElementQueryFailure? Failure,
    VisualTextLength? TotalLength = null,
    int Offset = 0,
    int ProbedLength = 0,
    bool IsEndRelativeOffsetBounded = false
)
{
    /// <summary>Gets whether this observation found another page.</summary>
    public bool HasMoreText => NextOffset is not null;

    /// <summary>
    /// Slices one observed text prefix at the requested UTF-16 offset without splitting an automatically generated page at a surrogate-pair boundary.
    /// </summary>
    public static VisualElementTextReadResult FromSuccess(string text, int offset, int maxCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);

        var resolvedOffset = offset >= 0 ? offset : (int)Math.Max(0L, (long)text.Length + offset);
        if (offset < 0 && resolvedOffset > 0 && resolvedOffset < text.Length &&
            char.IsLowSurrogate(text[resolvedOffset]) && char.IsHighSurrogate(text[resolvedOffset - 1])) resolvedOffset--;
        if (resolvedOffset >= text.Length)
        {
            return new VisualElementTextReadResult(
                string.Empty,
                null,
                null,
                VisualTextLength.Exact(text.Length),
                resolvedOffset,
                text.Length);
        }

        var end = (int)Math.Min((long)resolvedOffset + maxCharacters, text.Length);
        if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
        {
            end = end - resolvedOffset == 1 ? end + 1 : end - 1;
        }

        return new VisualElementTextReadResult(
            text[resolvedOffset..end],
            end < text.Length ? end : null,
            null,
            VisualTextLength.Exact(text.Length),
            resolvedOffset,
            text.Length);
    }

    /// <summary>Creates a failed text observation without continuation metadata.</summary>
    public static VisualElementTextReadResult FromFailure(VisualElementQueryFailure failure) => new(null, null, failure);
}