// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedMember.Local
// ReSharper disable MemberCanBePrivate.Global

using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using MonoMod;

namespace Everywhere.Patches.Avalonia.Base;

/// <summary>
/// This fixes https://github.com/DearVa/Everywhere/issues/313
/// Since TextLeadingPrefixCharacterEllipsis.Collapse is not virtual, we have to patch the method body to change the behavior of the collapsing logic.
/// It also calls internal class and methods that we cannot access directly.
/// </summary>
/// <remarks>
/// System.ArgumentOutOfRangeException: length must be greater than zero. (Parameter 'length')
///   at SplitResult{ShapedTextRun} ShapedTextRun.Split(int length)()
///   at TextRun[] TextLeadingPrefixCharacterEllipsis.Collapse(TextLine textLine)()
///   at TextLine TextLineImpl.Collapse(params TextCollapsingProperties[] collapsingPropertiesList)()
///   at TextLine[] TextLayout.CreateTextLines()()
///   at new TextLayout(ITextSource textSource, TextParagraphProperties paragraphProperties, TextTrimming textTrimming, double maxWidth, double maxHeight, int maxLines)()
///   at TextLayout TextBlock.CreateTextLayout(string text)()
///   at void TextBlock.RenderCore(DrawingContext context)()
///   at void CompositingRenderer.UpdateCore()()
///   at CompositionBatch Compositor.CommitCore()()
///   at CompositionBatch MediaContext.CommitCompositor(Compositor compositor)()
///   at void TopLevel.HandlePaint(Rect rect)()
///   at IntPtr WindowImpl.AppWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)()
///   at IntPtr PopupImpl.WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)()
///   at IntPtr WindowImpl.WndProcMessageHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)()
/// </remarks>
[MonoModPatch("Avalonia.Media.TextFormatting.TextLeadingPrefixCharacterEllipsis")]
public sealed class patch_TextLeadingPrefixCharacterEllipsis : TextCollapsingProperties
{
    [MonoModIgnore]
    public extern override double Width { get; }

    [MonoModIgnore]
    public extern override TextRun Symbol { get; }

    [MonoModIgnore]
    public extern override FlowDirection FlowDirection { get; }

    [MonoModIgnore]
    private int _prefixLength;

    [MonoModReplace]
    public override TextRun[]? Collapse(TextLine textLine)
    {
        var shapedSymbol = TextFormatter.CreateSymbol(Symbol, FlowDirection.LeftToRight);
        if (Width < shapedSymbol.GlyphRun.Bounds.Width)
        {
            return [];
        }

        var textRunEnumerator = new LogicalTextRunEnumerator(textLine);
        var availableWidth = Width - shapedSymbol.Size.Width;
        while (textRunEnumerator.MoveNext(out var run))
        {
            if (run is not DrawableTextRun drawableTextRun) continue;
            availableWidth -= drawableTextRun.Size.Width;

            if (!(availableWidth < 0)) continue;
            var objectPool = FormattingObjectPool.Instance;

            // meaningful order when this line contains RTL / reversed textRuns
            var innerTextRunEnumerator = new LogicalTextRunEnumerator(textLine);
            var textRuns = objectPool.TextRunLists.Rent();

            while (innerTextRunEnumerator.MoveNext(out var innerRun))
                textRuns.Add(innerRun);

            var collapsedRuns = objectPool.TextRunLists.Rent();
            FormattingObjectPool.RentedList<TextRun>? rentedPreSplitRuns = null;
            FormattingObjectPool.RentedList<TextRun>? rentedPostSplitRuns = null;

            try
            {
                FormattingObjectPool.RentedList<TextRun>? effectivePostSplitRuns;
                var availableSuffixWidth = Width - shapedSymbol.Size.Width;

                // prepare the prefix
                if (_prefixLength > 0)
                {
                    (rentedPreSplitRuns, rentedPostSplitRuns) = TextFormatterImpl.SplitTextRuns(textRuns, _prefixLength, objectPool);
                    effectivePostSplitRuns = rentedPostSplitRuns;
                    if (rentedPreSplitRuns != null)
                    {
                        foreach (var preSplitRun in rentedPreSplitRuns)
                        {
                            collapsedRuns.Add(preSplitRun);
                            if (preSplitRun is DrawableTextRun innerDrawableTextRun)
                            {
                                availableSuffixWidth -= innerDrawableTextRun.Size.Width;
                            }
                        }
                    }
                }
                else
                {
                    effectivePostSplitRuns = textRuns;
                }

                // add Ellipsis symbol
                collapsedRuns.Add(shapedSymbol);

                if (effectivePostSplitRuns is null || availableSuffixWidth <= 0)
                {
                    return collapsedRuns.ToArray();
                }

                var suffixStartIndex = collapsedRuns.Count;

                // append the suffix backwards until it gets trimmed
                for (var i = effectivePostSplitRuns.Count - 1; i >= 0; i--)
                {
                    var innerRun = effectivePostSplitRuns[i];

                    if (innerRun is ShapedTextRun endShapedRun)
                    {
                        if (endShapedRun.TryMeasureCharactersBackwards(
                                availableSuffixWidth,
                                out var suffixCount,
                                out var suffixWidth))
                        {
                            if (endShapedRun.IsReversed)
                                endShapedRun.Reverse();

                            availableSuffixWidth -= suffixWidth;

                            if (suffixCount >= innerRun.Length)
                            {
                                collapsedRuns.Insert(suffixStartIndex, innerRun);
                            }
                            else
                            {
                                var splitSuffix = endShapedRun.Split(innerRun.Length - suffixCount);
                                collapsedRuns.Insert(suffixStartIndex, splitSuffix.Second!);
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    else if (innerRun is DrawableTextRun innerDrawableTextRun)
                    {
                        availableSuffixWidth -= innerDrawableTextRun.Size.Width;

                        // entire run must fit
                        if (availableSuffixWidth >= 0)
                            collapsedRuns.Insert(suffixStartIndex, innerRun);
                        else
                            break;
                    }
                }

                return collapsedRuns.ToArray();
            }
            finally
            {
                objectPool.TextRunLists.Return(ref rentedPreSplitRuns);
                objectPool.TextRunLists.Return(ref rentedPostSplitRuns);
                objectPool.TextRunLists.Return(ref collapsedRuns);
                objectPool.TextRunLists.Return(ref textRuns);
            }
        }

        return null;
    }
}
