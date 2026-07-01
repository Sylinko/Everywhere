using Everywhere.AI;
using Everywhere.Interop;

namespace Everywhere.Chat;

partial class VisualContextBuilder
{
    private static bool ShouldIncludeBounds(VisualContextDetailLevel detailLevel, VisualElementType type) => detailLevel switch
    {
        VisualContextDetailLevel.Detailed => true,
        VisualContextDetailLevel.Compact when type is
            VisualElementType.TextEdit or
            VisualElementType.Button or
            VisualElementType.CheckBox or
            VisualElementType.ListView or
            VisualElementType.TreeView or
            VisualElementType.DataGrid or
            VisualElementType.TabControl or
            VisualElementType.Table or
            VisualElementType.Document or
            VisualElementType.TopLevel or
            VisualElementType.Screen => true,
        VisualContextDetailLevel.Minimal when type is
            VisualElementType.TopLevel or
            VisualElementType.Screen => true,
        _ => false
    };

    /// <summary>
    /// Determines if two strings are approximately equal, allowing for minor differences such as whitespace and punctuation.
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    private static bool ApproximatelyEquals(string? a, string? b)
    {
        var s1 = a.AsSpan();
        var s2 = b.AsSpan();

        for (int i = 0, j = 0; i < s1.Length && j < s2.Length;)
        {
            if (char.IsWhiteSpace(s1[i]) || char.IsPunctuation(s1[i]))
            {
                i++;
                continue;
            }

            if (char.IsWhiteSpace(s2[j]) || char.IsPunctuation(s2[j]))
            {
                j++;
                continue;
            }

            if (char.ToLowerInvariant(s1[i]) != char.ToLowerInvariant(s2[j]))
            {
                return false;
            }

            i++;
            j++;
        }

        return true;
    }

    /// <summary>
    /// Omits the middle part of the text if the estimated token count exceeds the specified maximum length.
    /// </summary>
    /// <param name="text"></param>
    /// <param name="maxLength"></param>
    /// <param name="omittedLength"></param>
    /// <returns></returns>
    private static string OmitIfNeeded(string text, int maxLength, out int omittedLength)
    {
        var tokenCount = TokenHelper.EstimateTokenCount(text);
        if (maxLength <= 0 || tokenCount <= maxLength)
        {
            omittedLength = 0;
            return text;
        }

        var approximateLength = text.Length * maxLength / tokenCount;
        omittedLength = Math.Max(0, (approximateLength - 3) / 2);
        return omittedLength > 0 ? $"{text[..omittedLength]}...omitted...{text[^omittedLength..]}" : text;
    }

    /// <summary>
    /// Computes the omission marker string for a visual element based on its omission state.
    /// Returns <c>null</c> when nothing is omitted (no overhead in serialized output).
    /// </summary>
    private static string? GetOmittedMarker(bool hasOmittedChildren, bool isContentTruncated) =>
        (hasOmittedChildren, isContentTruncated) switch
        {
            (true, true) => "children,content",
            (true, false) => "children",
            (false, true) => "content",
            _ => null
        };

    private static bool IsInteractiveElement(IVisualElement element)
    {
        if (element.Type is VisualElementType.Button or
            VisualElementType.Hyperlink or
            VisualElementType.CheckBox or
            VisualElementType.RadioButton or
            VisualElementType.ComboBox or
            VisualElementType.ListView or
            VisualElementType.ListViewItem or
            VisualElementType.TreeView or
            VisualElementType.TreeViewItem or
            VisualElementType.DataGrid or
            VisualElementType.DataGridItem or
            VisualElementType.TabControl or
            VisualElementType.TabItem or
            VisualElementType.Menu or
            VisualElementType.MenuItem or
            VisualElementType.Slider or
            VisualElementType.ScrollBar or
            VisualElementType.ProgressBar or
            VisualElementType.TextEdit or
            VisualElementType.Table or
            VisualElementType.TableRow) return true;

        return (element.States & InteractiveStates) != 0;
    }
}