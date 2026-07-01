using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.ML.Tokenizers;

namespace Everywhere.AI;

public static class TokenHelper
{
    public enum OmitPosition
    {
        Middle,
        Start,
        End
    }

    private static readonly TiktokenTokenizer Tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public const string DefaultOmitText = "[... {0:N0} chars omitted ...]";

    /// <summary>
    /// Formats the omit message by injecting omitted counts into placeholder slots.
    /// Placeholders: {0} = omitted chars, {1} = omitted tokens.
    /// When omitText contains no '{', returns it as-is — zero overhead on the hot path.
    /// </summary>
    private static string FormatOmitText(string omitText, int omittedChars, int omittedTokens)
    {
        if (!omitText.Contains('{')) return omitText;

        return omitText.Contains("{1}")
            ? string.Format(omitText, omittedChars, omittedTokens)
            : string.Format(omitText, omittedChars);
    }

    /// <summary>
    /// Approximates the number of LLM tokens for a given string.
    /// </summary>
    /// <param name="text">The input string to calculate the token count for.</param>
    /// <returns>An approximate number of tokens.</returns>
    public static int EstimateTokenCount(string text)
    {
        return string.IsNullOrEmpty(text) ? 0 : Tokenizer.CountTokens(text);
    }

    /// <summary>
    /// Omits parts of the input text to ensure the total token count does not exceed the specified maximum.
    /// Note that omitText is not included in the token count.
    /// <para>
    /// The <paramref name="omitText"/> string may contain format placeholders:
    /// <list type="bullet">
    ///   <item><c>{0}</c> — number of omitted characters (e.g. "[... {0:N0} chars omitted ...]")</item>
    ///   <item><c>{1}</c> — approximate number of omitted tokens</item>
    /// </list>
    /// If no placeholders are present, the string is used verbatim.
    /// </para>
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Omit(
        string? text,
        int maxTokenCount = 8000,
        string omitText = DefaultOmitText,
        OmitPosition position = OmitPosition.Middle)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var totalTokens = Tokenizer.CountTokens(text);
        if (totalTokens <= maxTokenCount) return text;

        switch (position)
        {
            case OmitPosition.Middle:
            {
                var startIndex = Tokenizer.GetIndexByTokenCount(text, maxTokenCount / 2, out _, out _);
                var endIndex = Tokenizer.GetIndexByTokenCountFromEnd(text, maxTokenCount / 2, out _, out _);
                var formatted = FormatOmitText(omitText, endIndex - startIndex, totalTokens - maxTokenCount);
                return string.Concat(text.AsSpan(0, startIndex), formatted, text.AsSpan(endIndex));
            }
            case OmitPosition.Start:
            {
                var endIndex = Tokenizer.GetIndexByTokenCountFromEnd(text, maxTokenCount, out _, out _);
                var formatted = FormatOmitText(omitText, endIndex, totalTokens - maxTokenCount);
                return string.Concat(formatted, text.AsSpan(endIndex));
            }
            case OmitPosition.End:
            {
                var startIndex = Tokenizer.GetIndexByTokenCount(text, maxTokenCount, out _, out _);
                var formatted = FormatOmitText(omitText, text.Length - startIndex, totalTokens - maxTokenCount);
                return string.Concat(text.AsSpan(0, startIndex), formatted);
            }
            default:
            {
                return text;
            }
        }
    }

    /// <summary>
    /// Omits parts of the input text to ensure the total token count does not exceed the specified maximum,
    /// appending the result to a <see cref="StringBuilder"/>.
    /// Note that omitText is not included in the token count.
    /// <para>
    /// The <paramref name="omitText"/> string may contain format placeholders:
    /// <list type="bullet">
    ///   <item><c>{0}</c> — number of omitted characters</item>
    ///   <item><c>{1}</c> — approximate number of omitted tokens</item>
    /// </list>
    /// If no placeholders are present, the string is used verbatim.
    /// </para>
    /// </summary>
    /// <returns>Actual token count of the appended text (which may be slightly less than maxTokenCount due to omitText length).</returns>
    public static int OmitTo(
        string? text,
        StringBuilder resultBuilder,
        int maxTokenCount = 8000,
        string omitText = DefaultOmitText,
        OmitPosition position = OmitPosition.Middle)
    {
        if (string.IsNullOrEmpty(text))
        {
            resultBuilder.Append(text);
            return 0;
        }

        var totalTokens = Tokenizer.CountTokens(text);
        if (totalTokens <= maxTokenCount)
        {
            resultBuilder.Append(text);
            return totalTokens;
        }

        switch (position)
        {
            case OmitPosition.Middle:
            {
                var startIndex = Tokenizer.GetIndexByTokenCount(text, maxTokenCount / 2, out _, out _);
                var endIndex = Tokenizer.GetIndexByTokenCountFromEnd(text, maxTokenCount / 2, out _, out _);
                resultBuilder.Append(text, 0, startIndex);
                resultBuilder.Append(FormatOmitText(omitText, endIndex - startIndex, totalTokens - maxTokenCount));
                resultBuilder.Append(text, endIndex, text.Length - endIndex);
                break;
            }
            case OmitPosition.Start:
            {
                var endIndex = Tokenizer.GetIndexByTokenCountFromEnd(text, maxTokenCount, out _, out _);
                resultBuilder.Append(FormatOmitText(omitText, endIndex, totalTokens - maxTokenCount));
                resultBuilder.Append(text, endIndex, text.Length - endIndex);
                break;
            }
            case OmitPosition.End:
            {
                var startIndex = Tokenizer.GetIndexByTokenCount(text, maxTokenCount, out _, out _);
                resultBuilder.Append(text, 0, startIndex);
                resultBuilder.Append(FormatOmitText(omitText, text.Length - startIndex, totalTokens - maxTokenCount));
                break;
            }
            default:
            {
                resultBuilder.Append(text);
                break;
            }
        }

        return maxTokenCount;
    }
}