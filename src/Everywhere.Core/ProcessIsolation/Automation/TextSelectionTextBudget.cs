using System.Buffers;
using System.Text;

namespace Everywhere.ProcessIsolation.Automation;

// TODO: apply this to common visual element text?
/// <summary>Bounds selected text so its complete notification remains below the ordinary RPC frame ceiling.</summary>
public static class TextSelectionTextBudget
{
    /// <summary>Maximum UTF-8 bytes reserved for selected text inside the current 1 MiB frame.</summary>
    public const int MaximumUtf8Bytes = 768 * 1024;

    /// <summary>Maximum UTF-16 code units requested from native providers before the final byte check.</summary>
    public const int MaximumNativeReadCharacters = MaximumUtf8Bytes;

    /// <summary>Applies the transport budget while preserving complete Unicode scalar boundaries.</summary>
    public static TextSelectionText Apply(string text, bool isNativeLimitReached = false)
    {
        if (Encoding.UTF8.GetByteCount(text) <= MaximumUtf8Bytes)
        {
            return new TextSelectionText(text, isNativeLimitReached);
        }

        var consumedCharacters = 0;
        var consumedBytes = 0;
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var runeCharacters);
            if (status is not OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                runeCharacters = 1;
            }

            if (consumedBytes > MaximumUtf8Bytes - rune.Utf8SequenceLength) break;
            consumedBytes += rune.Utf8SequenceLength;
            consumedCharacters += runeCharacters;
            remaining = remaining[runeCharacters..];
        }

        return new TextSelectionText(text[..consumedCharacters], true);
    }
}

/// <summary>Contains selected text and whether a bounded read may have omitted trailing content.</summary>
public readonly record struct TextSelectionText(string? Text, bool IsIncomplete)
{
    /// <summary>Gets whether this value contains nonempty text.</summary>
    public bool HasText => !string.IsNullOrEmpty(Text);
}