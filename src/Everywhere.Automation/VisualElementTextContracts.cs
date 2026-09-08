namespace Everywhere.Automation;

/// <summary>
/// Contains one best-effort page of visual-element text and its next numeric offset.
/// </summary>
/// <param name="Text">The observed page, or <see langword="null" /> when no textual capability was available.</param>
/// <param name="NextOffset">The next UTF-16 offset, or <see langword="null" /> when this observation found no more text.</param>
/// <param name="Failure">The normalized provider failure, if one occurred.</param>
/// <remarks>
/// The offset is stateless and does not make a changing visual tree immutable. Concurrent content changes may cause overlap or omission between pages.
/// </remarks>
public sealed record VisualElementTextReadResult(string? Text, int? NextOffset, VisualElementQueryFailure? Failure)
{
    /// <summary>Gets whether this observation found another page.</summary>
    public bool HasMoreText => NextOffset is not null;

    /// <summary>
    /// Slices one observed text prefix at the requested UTF-16 offset without splitting an automatically generated page at a surrogate-pair boundary.
    /// </summary>
    public static VisualElementTextReadResult FromSuccess(string text, int offset, int maxCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);

        if (offset >= text.Length)
        {
            return new VisualElementTextReadResult(string.Empty, null, null);
        }

        var end = (int)Math.Min((long)offset + maxCharacters, text.Length);
        if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
        {
            end = end - offset == 1 ? end + 1 : end - 1;
        }

        return new VisualElementTextReadResult(text[offset..end], end < text.Length ? end : null, null);
    }

    public static VisualElementTextReadResult FromFailure(VisualElementQueryFailure failure) => new(null, null, failure);
}