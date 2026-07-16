using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Everywhere.AI.Prompts;
using ZLinq;

namespace Everywhere.Views;

/// <summary>
/// Displays prompt template text with lightweight placeholder highlighting.
/// </summary>
/// <remarks>
/// The control stays read-only and deliberately inherits <see cref="SelectableTextBlock"/> so
/// prompt previews keep normal text selection and copy behavior. <see cref="PromptText"/> drives
/// <see cref="TextBlock.Inlines"/>; callers should not set <see cref="TextBlock.Text"/> at the
/// same time because the two content models are mutually exclusive in practice.
/// </remarks>
public sealed class PromptTemplateTextBlock : SelectableTextBlock
{
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public static readonly StyledProperty<string?> PromptTextProperty =
        AvaloniaProperty.Register<PromptTemplateTextBlock, string?>(nameof(PromptText));

    public static readonly StyledProperty<IReadOnlyList<PromptTemplateRenderSegment>?> PromptSegmentsProperty =
        AvaloniaProperty.Register<PromptTemplateTextBlock, IReadOnlyList<PromptTemplateRenderSegment>?>(nameof(PromptSegments));

    private readonly PromptTemplateInlineSynchronizer _inlineSynchronizer = new();

    /// <summary>
    /// Text to render and highlight as a prompt template.
    /// </summary>
    public string? PromptText
    {
        get => GetValue(PromptTextProperty);
        set => SetValue(PromptTextProperty, value);
    }

    /// <summary>
    /// Already-rendered preview segments with placeholder source metadata.
    /// </summary>
    public IReadOnlyList<PromptTemplateRenderSegment>? PromptSegments
    {
        get => GetValue(PromptSegmentsProperty);
        set => SetValue(PromptSegmentsProperty, value);
    }

    static PromptTemplateTextBlock()
    {
        RequestBringIntoViewEvent.AddClassHandler<PromptTemplateTextBlock>((_, args) => args.Handled = true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PromptTextProperty && PromptSegments is null)
        {
            var inlines = Inlines ??= [];
            _inlineSynchronizer.SyncTemplate(inlines, change.GetNewValue<string?>());
        }
        else if (change.Property == PromptSegmentsProperty)
        {
            var inlines = Inlines ??= [];
            _inlineSynchronizer.SyncRendered(inlines, change.GetNewValue<IReadOnlyList<PromptTemplateRenderSegment>?>());
        }
    }
}

/// <summary>
/// Synchronizes prompt-template segments into a host inline collection while reusing existing runs.
/// </summary>
/// <remarks>
/// Parsing the full text on each change is cheap for the prompt preview sizes we render here. The
/// stateful part is the UI object synchronization: unchanged prefix/suffix runs are preserved, and
/// changed middle runs are updated in place when their highlight kind stays the same.
/// </remarks>
internal sealed class PromptTemplateInlineSynchronizer(IReadOnlySet<string> knownPlaceholderNames)
{
    private readonly List<InlineState> _states = [];

    public PromptTemplateInlineSynchronizer() : this(
        SystemPromptPlaceholderSource.Instance.Definitions
            .Select(static definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal))
    {
    }

    /// <summary>
    /// Applies the latest prompt text to <paramref name="inlines"/>.
    /// </summary>
    /// <remarks>
    /// The synchronizer owns the run instances it creates. If the host collection is changed by
    /// another caller, the next sync falls back to a full rebuild to recover a consistent state.
    /// </remarks>
    public void SyncTemplate(InlineCollection inlines, string? text)
    {
        Sync(inlines, BuildTemplateSegments(text ?? string.Empty));
    }

    public void SyncRendered(InlineCollection inlines, IReadOnlyList<PromptTemplateRenderSegment>? renderSegments)
    {
        Sync(inlines, BuildRenderedSegments(renderSegments ?? []));
    }

    private void Sync(InlineCollection inlines, IReadOnlyList<PromptTemplateInlineSegment> segments)
    {
        if (!IsInlineCollectionOwned(inlines))
        {
            Rebuild(inlines, segments);
            return;
        }

        var prefixLength = CountEqualPrefix(segments);
        var suffixLength = CountEqualSuffix(segments, prefixLength);

        var oldMiddleCount = _states.Count - prefixLength - suffixLength;
        var newMiddleCount = segments.Count - prefixLength - suffixLength;
        var reusableMiddleCount = Math.Min(oldMiddleCount, newMiddleCount);

        for (var i = 0; i < reusableMiddleCount; i++)
        {
            var stateIndex = prefixLength + i;
            var segment = segments[stateIndex];
            var oldState = _states[stateIndex];

            if (oldState.Segment.Kind == segment.Kind)
            {
                oldState.Run.Text = segment.Text;
                if (!HasSameVisuals(oldState.Segment, segment))
                {
                    ApplyVisuals(oldState.Run, segment);
                }

                _states[stateIndex] = oldState with { Segment = segment };
            }
            else
            {
                var newState = CreateState(segment);
                inlines[stateIndex] = newState.Run;
                _states[stateIndex] = newState;
            }
        }

        var removeCount = oldMiddleCount - reusableMiddleCount;
        for (var i = 0; i < removeCount; i++)
        {
            var removeIndex = prefixLength + reusableMiddleCount;
            inlines.RemoveAt(removeIndex);
            _states.RemoveAt(removeIndex);
        }

        var insertCount = newMiddleCount - reusableMiddleCount;
        for (var i = 0; i < insertCount; i++)
        {
            var insertIndex = prefixLength + reusableMiddleCount + i;
            var newState = CreateState(segments[insertIndex]);
            inlines.Insert(insertIndex, newState.Run);
            _states.Insert(insertIndex, newState);
        }
    }

    private bool IsInlineCollectionOwned(InlineCollection inlines)
    {
        return inlines.Count == _states.Count && !_states.AsValueEnumerable().Where((t, i) => !ReferenceEquals(inlines[i], t.Run)).Any();
    }

    private void Rebuild(InlineCollection inlines, IReadOnlyList<PromptTemplateInlineSegment> segments)
    {
        inlines.Clear();
        _states.Clear();

        foreach (var segment in segments.AsValueEnumerable())
        {
            var state = CreateState(segment);
            inlines.Add(state.Run);
            _states.Add(state);
        }
    }

    private int CountEqualPrefix(IReadOnlyList<PromptTemplateInlineSegment> segments)
    {
        var count = Math.Min(_states.Count, segments.Count);
        var index = 0;
        while (index < count && _states[index].Segment == segments[index])
        {
            index++;
        }

        return index;
    }

    private int CountEqualSuffix(IReadOnlyList<PromptTemplateInlineSegment> segments, int prefixLength)
    {
        var maxCount = Math.Min(_states.Count, segments.Count) - prefixLength;
        var suffixLength = 0;
        while (suffixLength < maxCount)
        {
            var oldIndex = _states.Count - suffixLength - 1;
            var newIndex = segments.Count - suffixLength - 1;
            if (_states[oldIndex].Segment != segments[newIndex])
            {
                break;
            }

            suffixLength++;
        }

        return suffixLength;
    }

    private static InlineState CreateState(PromptTemplateInlineSegment segment)
    {
        var run = new Run(segment.Text);
        ApplyVisuals(run, segment);

        return new InlineState(segment, run);
    }

    private static void ApplyVisuals(Run run, PromptTemplateInlineSegment segment)
    {
        ClearPromptVisuals(run);
        if (segment.PlaceholderName is null)
        {
            return;
        }

        run.Classes.Add("PromptPlaceholder");
        if (segment.PlaceholderName == SystemPromptPlaceholderSource.DefaultSystemPromptName)
        {
            run.Classes.Add("DefaultSystemPrompt");
        }

        if (segment.Kind == PromptTemplateInlineSegmentKind.UnknownPlaceholder)
        {
            run.Classes.Add("Unknown");
        }

        run.Foreground = PromptPlaceholderPalette.GetBrush(segment.PlaceholderName, segment.ColorSlot);
    }

    private static bool HasSameVisuals(PromptTemplateInlineSegment oldSegment, PromptTemplateInlineSegment newSegment) =>
        oldSegment.Kind == newSegment.Kind &&
        oldSegment.PlaceholderName == newSegment.PlaceholderName &&
        oldSegment.ColorSlot == newSegment.ColorSlot;

    private static void ClearPromptVisuals(Run run)
    {
        run.Classes.Remove("PromptPlaceholder");
        run.Classes.Remove("DefaultSystemPrompt");
        run.Classes.Remove("Unknown");
        run.ClearValue(Run.ForegroundProperty);
    }

    private IReadOnlyList<PromptTemplateInlineSegment> BuildTemplateSegments(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var placeholders = PromptTemplateParser.ParsePlaceholders(text);
        if (placeholders.Count == 0)
        {
            return [PromptTemplateInlineSegment.Plain(text)];
        }

        var segments = new List<PromptTemplateInlineSegment>(placeholders.Count * 2 + 1);
        var cursor = 0;
        foreach (var placeholder in placeholders)
        {
            if (placeholder.Span.Start > cursor)
            {
                segments.Add(PromptTemplateInlineSegment.Plain(text[cursor..placeholder.Span.Start]));
            }

            var kind = knownPlaceholderNames.Contains(placeholder.Name) ?
                PromptTemplateInlineSegmentKind.KnownPlaceholder :
                PromptTemplateInlineSegmentKind.UnknownPlaceholder;
            segments.Add(PromptTemplateInlineSegment.Placeholder(placeholder.RawText, placeholder.Name, kind));
            cursor = placeholder.Span.Start + placeholder.Span.Length;
        }

        if (cursor < text.Length)
        {
            segments.Add(PromptTemplateInlineSegment.Plain(text[cursor..]));
        }

        return PromptPlaceholderPalette.AssignColorSlots(segments);
    }

    private static IReadOnlyList<PromptTemplateInlineSegment> BuildRenderedSegments(
        IReadOnlyList<PromptTemplateRenderSegment> renderSegments)
    {
        if (renderSegments.Count == 0)
        {
            return [];
        }

        var segments = renderSegments
            .AsValueEnumerable()
            .Where(static segment => segment.Text.Length > 0)
            .Select(static segment => segment.PlaceholderName is null ?
                PromptTemplateInlineSegment.Plain(segment.Text) :
                PromptTemplateInlineSegment.Placeholder(
                    segment.Text,
                    segment.PlaceholderName,
                    segment.Kind == PromptTemplateRenderSegmentKind.UnresolvedPlaceholder ?
                        PromptTemplateInlineSegmentKind.UnknownPlaceholder :
                        PromptTemplateInlineSegmentKind.KnownPlaceholder))
            .ToList();
        return PromptPlaceholderPalette.AssignColorSlots(segments);
    }

    private readonly record struct InlineState(PromptTemplateInlineSegment Segment, Run Run);

    internal readonly record struct PromptTemplateInlineSegment(
        string Text,
        PromptTemplateInlineSegmentKind Kind,
        string? PlaceholderName,
        int ColorSlot
    )
    {
        public static PromptTemplateInlineSegment Plain(string text) =>
            new(text, PromptTemplateInlineSegmentKind.Plain, null, PromptPlaceholderPalette.NoColorSlot);

        public static PromptTemplateInlineSegment Placeholder(
            string text,
            string placeholderName,
            PromptTemplateInlineSegmentKind kind) =>
            new(text, kind, placeholderName, PromptPlaceholderPalette.UnassignedColorSlot);

        public PromptTemplateInlineSegment WithColorSlot(int colorSlot) => this with { ColorSlot = colorSlot };
    }

    internal enum PromptTemplateInlineSegmentKind
    {
        Plain,
        KnownPlaceholder,
        UnknownPlaceholder
    }
}

/// <summary>
/// Stable palette for prompt placeholder highlighting.
/// </summary>
/// <remarks>
/// The palette intentionally uses fixed colors instead of theme resources so the same placeholder
/// keeps a recognizable color across raw template, preview, and future editor surfaces.
/// </remarks>
internal static class PromptPlaceholderPalette
{
    public const int NoColorSlot = -2;
    public const int UnassignedColorSlot = -1;
    public const int DefaultSystemPromptColorSlot = -3;

    private static readonly IBrush DefaultSystemPromptBrush = new SolidColorBrush(Color.Parse("#8A8A8A"));

    private static readonly IBrush[] Brushes =
    [
        new SolidColorBrush(Color.Parse("#2563EB")),
        new SolidColorBrush(Color.Parse("#16A34A")),
        new SolidColorBrush(Color.Parse("#DC2626")),
        new SolidColorBrush(Color.Parse("#9333EA")),
        new SolidColorBrush(Color.Parse("#0891B2")),
        new SolidColorBrush(Color.Parse("#EA580C")),
        new SolidColorBrush(Color.Parse("#DB2777")),
        new SolidColorBrush(Color.Parse("#65A30D")),
        new SolidColorBrush(Color.Parse("#7C3AED")),
        new SolidColorBrush(Color.Parse("#0D9488")),
        new SolidColorBrush(Color.Parse("#CA8A04")),
        new SolidColorBrush(Color.Parse("#0284C7"))
    ];

    public static IBrush GetBrush(string placeholderName, int colorSlot)
    {
        if (placeholderName == SystemPromptPlaceholderSource.DefaultSystemPromptName)
        {
            return DefaultSystemPromptBrush;
        }

        var slot = colorSlot >= 0 ? colorSlot : GetStableSlot(placeholderName);
        return Brushes[slot % Brushes.Length];
    }

    public static IReadOnlyList<PromptTemplateInlineSynchronizer.PromptTemplateInlineSegment> AssignColorSlots(
        IReadOnlyList<PromptTemplateInlineSynchronizer.PromptTemplateInlineSegment> segments)
    {
        if (segments.Count == 0)
        {
            return segments;
        }

        var assignedSlots = AssignColorSlots(
            segments
                .AsValueEnumerable()
                .Select(static segment => segment.PlaceholderName)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList());

        var result = new PromptTemplateInlineSynchronizer.PromptTemplateInlineSegment[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            result[i] = segment.PlaceholderName is null ?
                segment :
                segment.WithColorSlot(assignedSlots[segment.PlaceholderName]);
        }

        return result;
    }

    public static IReadOnlyDictionary<string, int> AssignColorSlots(IEnumerable<string> placeholderNames) =>
        AssignSlots(placeholderNames.Distinct(StringComparer.Ordinal).ToList());

    private static Dictionary<string, int> AssignSlots(IReadOnlyList<string> placeholderNames)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var usedSlots = new bool[Brushes.Length];

        foreach (var placeholderName in placeholderNames.OrderBy(GetStableHash))
        {
            if (placeholderName == SystemPromptPlaceholderSource.DefaultSystemPromptName)
            {
                result[placeholderName] = DefaultSystemPromptColorSlot;
                continue;
            }

            var slot = GetStableSlot(placeholderName);
            if (placeholderNames.Count <= Brushes.Length)
            {
                while (usedSlots[slot])
                {
                    slot = (slot + 1) % Brushes.Length;
                }

                usedSlots[slot] = true;
            }

            result[placeholderName] = slot;
        }

        return result;
    }

    private static int GetStableSlot(string placeholderName) =>
        (int)(GetStableHash(placeholderName) % (uint)Brushes.Length);

    private static uint GetStableHash(string text)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;

        var hash = offset;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= prime;
        }

        return hash;
    }
}
