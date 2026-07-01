using System.Reactive.Disposables;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicData;
using Everywhere.Collections;
using Everywhere.Common;
using MessagePack;
using ZLinq;

namespace Everywhere.Chat.Plugins;

public enum TextChangeKind
{
    Insert = 0,
    Delete = 1,
    Replace = 2
}

/// <summary>
/// A half-open character range over the original text \[Start, End).
/// Offsets are 0-based and refer to the original file content.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public readonly partial record struct TextRange
{
    [Key(0)]
    public int Start { get; }

    [Key(1)]
    public int Length { get; }

    public int End => Start + Length;

    [SerializationConstructor]
    public TextRange(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Start = start;
        Length = length;
    }

    public static TextRange FromBounds(int start, int end)
    {
        if (end < start) throw new ArgumentOutOfRangeException(nameof(end));
        return new TextRange(start, end - start);
    }

    public void EnsureInside(string original)
    {
        if (Start > original.Length || End > original.Length)
            throw new ArgumentOutOfRangeException($"Range [{Start},{End}) is outside original length {original.Length}.");
    }

    public override string ToString() => $"[{Start},{End})";
}

/// <summary>
/// A single edit on the original text. Offsets refer to the original content.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public sealed partial class TextChange : ObservableObject
{
    [Key(0)]
    public string Id { get; set; } = Guid.CreateVersion7().ToString("N");

    [Key(1)]
    public TextChangeKind Kind { get; set; }

    [Key(2)]
    public TextRange Range { get; set; } = new(0, 0);

    /// <summary>
    /// Replacement text for Insert/Replace; null for Delete.
    /// </summary>
    [Key(3)]
    public string? NewText { get; set; }

    /// <summary>
    /// Is the change accepted (true), rejected (false), or undecided (null) by user?
    /// </summary>
    [Key(4)]
    [ObservableProperty]
    public partial bool? Accepted { get; set; }

    public static TextChange Insert(int at, string? text) => new()
    {
        Kind = TextChangeKind.Insert,
        Range = new TextRange(at, 0),
        NewText = text
    };

    public static TextChange Delete(int start, int length) => new()
    {
        Kind = TextChangeKind.Delete,
        Range = new TextRange(start, length)
    };

    public static TextChange Replace(int start, int length, string? newText) => new()
    {
        Kind = TextChangeKind.Replace,
        Range = new TextRange(start, length),
        NewText = newText
    };

    public string GetOriginalSlice(string original)
    {
        Range.EnsureInside(original);
        return original.Substring(Range.Start, Range.Length);
    }

    public override string ToString()
        => $"{Kind} id={Id} range={Range} accepted={Accepted?.ToString().ToLowerInvariant() ?? "null"}";
}

/// <summary>
/// Defines a text difference between two versions of text.
/// </summary>
/// <remarks>
/// This record is not used for serialization; use ToString() for text representation.
/// </remarks>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class TextDifference : ObservableObject, IDisposable
{
    [Key(0)]
    public string FilePath { get; }

    /// <summary>
    /// A read-only, thread-safe collection of changes for UI binding.
    /// </summary>
    [IgnoreMember]
    public IReadOnlyBindableList<TextChange> Changes { get; }

    /// <summary>
    /// For serialization purposes only.
    /// </summary>
    [Key(1)]
    private IEnumerable<TextChange> SerializableChanges
    {
        get => _changesSource.Items;
        set => _changesSource.Edit(list => list.Reset(value));
    }

    /// <summary>
    /// Indicates whether any changes are accepted (true), all discarded (false), or some pending (null).
    /// </summary>
    public bool? Acceptance
    {
        get
        {
            bool? acceptance = false;
            _changesSource.Edit(list =>
            {
                foreach (var change in list)
                {
                    if (change.Accepted is null)
                    {
                        acceptance = null;
                        return;
                    }

                    if (change.Accepted.Value) acceptance = true;
                }
            });

            return acceptance;
        }
    }

    public int TotalChangesCount => _changesSource.Count;

    /// <summary>
    /// Count of changes that are not pending (i.e., Accepted is true or false).
    /// </summary>
    public int NotPendingChangesCount => _changesSource.Count(c => c.Accepted.HasValue);

    [IgnoreMember] private readonly CompositeDisposable _disposables = new(3);
    [IgnoreMember] private readonly SourceList<TextChange> _changesSource = new();

    [IgnoreMember] private TaskCompletionSource<bool>? _acceptanceTcs;

    public TextDifference(string filePath)
    {
        FilePath = filePath;

        _changesSource.Connect()
            .WhenPropertyChanged(x => x.Accepted)
            .Subscribe(_ =>
            {
                NotifyChangesPropertiesChanged();
                TrySetAcceptanceResult();
            })
            .AddTo(_disposables);

        Changes = _changesSource.Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);

        _disposables.Add(_changesSource);
    }

    private void NotifyChangesPropertiesChanged()
    {
        OnPropertyChanged(nameof(Acceptance));
        OnPropertyChanged(nameof(TotalChangesCount));
        OnPropertyChanged(nameof(NotPendingChangesCount));
    }

    public void Add(params TextChange[] changes)
    {
        _changesSource.AddRange(changes);
    }

    public void AcceptAll() => SetAll(true);

    public void DiscardAll() => SetAll(false);

    /// <summary>
    /// Wait until at least one change is accepted or all are rejected.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<bool> WaitForAcceptanceAsync(CancellationToken cancellationToken = default)
    {
        var tcs = _acceptanceTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        TrySetAcceptanceResult(); // Check once in case already decided
        return tcs.Task;
    }

    internal void TrySetAcceptanceResult()
    {
        var acceptance = Acceptance;
        if (!acceptance.HasValue) return;
        _acceptanceTcs?.TrySetResult(acceptance.Value);
        _acceptanceTcs = null;
    }

    /// <summary>
    /// Get changes filtered according to the given options.
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public IEnumerable<TextChange> GetFilteredChanges(in TextDifferenceRenderOptions options)
    {
        IEnumerable<TextChange> q = _changesSource.Items;
        if (options.OnlyAccepted) q = q.Where(c => c.Accepted == true);
        else if (!options.IncludePending) q = q.Where(c => c.Accepted.HasValue);
        return q.OrderBy(c => c.Range.Start);
    }

    public void ValidateAgainst(string original)
    {
        foreach (var c in _changesSource.Items) c.Range.EnsureInside(original);
        var ordered = _changesSource.Items.OrderBy(c => c.Range.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var curr = ordered[i];
            if (prev.Range.End > curr.Range.Start)
                throw new InvalidOperationException($"Overlapping changes: {prev.Id} {prev.Range} and {curr.Id} {curr.Range}");
        }
    }

    public string Apply(string original, Func<TextChange, bool>? selector = null, bool validate = true)
    {
        if (validate) ValidateAgainst(original);
        var selected = _changesSource.Items
            .Where(c => selector?.Invoke(c) ?? c.Accepted == true)
            .OrderBy(c => c.Range.Start)
            .ToList();

        var sb = new StringBuilder();
        var cursor = 0;
        foreach (var c in selected)
        {
            sb.Append(original, cursor, c.Range.Start - cursor);
            sb.Append(c.NewText);
            cursor = c.Range.End;
        }
        sb.Append(original, cursor, original.Length - cursor);
        return sb.ToString();
    }

    public string ToUnifiedDiff(string original, in TextDifferenceRenderOptions options)
        => TextDifferenceRenderer.ToUnifiedDiff(this, original, options);

    public string ToModelSummary(string original, in TextDifferenceRenderOptions options)
        => TextDifferenceRenderer.ToModelSummary(this, original, options);

    private void SetAll(bool accepted)
    {
        _changesSource.Edit(list =>
        {
            foreach (var c in list) c.Accepted = accepted;
        });
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}

/// <summary>
/// Options for rendering TextDifference.
/// </summary>
/// <param name="OnlyAccepted">When true, only output changes with Accepted==true.</param>
/// <param name="IncludePending">When true, include changes with Accepted==null (pending).</param>
/// <param name="MaxPreviewLinesPerChange">Max lines to include for before/after preview of each change (0=unlimited).</param>
public readonly record struct TextDifferenceRenderOptions(
    bool OnlyAccepted = false,
    bool IncludePending = true,
    int MaxPreviewLinesPerChange = 200
);

/// <summary>
/// Render TextDifference into unified-diff style or LLM-friendly summary.
/// </summary>
public static class TextDifferenceRenderer
{
    public static string ToUnifiedDiff(TextDifference diff, string original, in TextDifferenceRenderOptions options = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- a/{diff.FilePath}");
        sb.AppendLine($"+++ b/{diff.FilePath}");

        foreach (var ch in diff.GetFilteredChanges(options))
        {
            var before = ch.GetOriginalSlice(original);
            var after = ch.NewText ?? string.Empty;

            var startLine = OffsetToLine(original, ch.Range.Start);
            var origLines = CountLines(before);
            var newLines = CountLines(after);

            sb.AppendLine(
                $"@@ -{startLine},{origLines} +{startLine},{newLines} " +
                $"@@ {ch.Kind} id={ch.Id[..6]} accepted={ch.Accepted?.ToString().ToLowerInvariant() ?? "null"}");

            foreach (var line in TakeLines(before, options.MaxPreviewLinesPerChange))
                sb.AppendLine($"- {line}");
            foreach (var line in TakeLines(after, options.MaxPreviewLinesPerChange))
                sb.AppendLine($"+ {line}");
        }
        return sb.ToString();
    }

    public static string ToModelSummary(TextDifference diff, string original, in TextDifferenceRenderOptions options = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"diff file: {diff.FilePath}");
        foreach (var ch in diff.GetFilteredChanges(options))
        {
            if (original.IsNullOrWhiteSpace() && ch.NewText.IsNullOrWhiteSpace())
            {
                continue;
            }

            sb.AppendLine(
                $"id={ch.Id[..6]} kind={ch.Kind} accepted={ch.Accepted?.ToString() ?? "False"} span={ch.Range.Start}:{ch.Range.Length}");
            sb.AppendLine("before<<<");
            sb.Append(ch.GetOriginalSlice(original));
            sb.AppendLine();
            sb.AppendLine(">>>");
            sb.AppendLine("after<<<");
            sb.Append(ch.NewText ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine(">>>");
        }
        sb.AppendLine("enddiff");
        return sb.ToString();
    }

    private static int OffsetToLine(string text, int offset)
    {
        if (offset < 0 || offset > text.Length) throw new ArgumentOutOfRangeException(nameof(offset));

        var lineBreakChar = Environment.NewLine.Contains('\n') ? '\n' : '\r';
        return 1 + text.AsSpan(0, offset).Count(lineBreakChar);
    }

    public static int CountLines(string? s) =>
        s.IsNullOrEmpty() ? 0 : TakeLines(s, -1).AsValueEnumerable().Where(l => l.Length > 0).Count();

    private static IEnumerable<string> TakeLines(string s, int maxLines)
    {
        using var reader = new StringReader(s);
        while (maxLines-- != 0) // use != to allow -1 (unlimited)
        {
            var line = reader.ReadLine();
            if (line is null) yield break;
            yield return line;
        }
    }
}

/// <summary>
/// Implements Myers' diff algorithm to compute differences between two sequences.
/// </summary>
internal static class MyersDifference
{
    internal enum EditKind
    {
        Equal,
        Insert,
        Delete,
        Replace
    }

    internal readonly record struct Edit(EditKind Kind, int AStart, int AEnd, int BStart, int BEnd);

    public static List<Edit> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = a.Count, m = b.Count, max = n + m;
        var trace = new List<Dictionary<int, int>>();
        var v = new Dictionary<int, int> { [1] = 0 };

        for (var d = 0; d <= max; d++)
        {
            var vv = new Dictionary<int, int>();
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || k != d && Get(v, k - 1) < Get(v, k + 1))
                    x = Get(v, k + 1);
                else
                    x = Get(v, k - 1) + 1;

                var y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                vv[k] = x;

                if (x < n || y < m) continue;

                trace.Add(vv);
                return Backtrack(trace, a, b);
            }
            trace.Add(vv);
            v = vv;
        }

        return [];

        static int Get(Dictionary<int, int> d, int k) => d.GetValueOrDefault(k, 0);
    }

    private static List<Edit> Backtrack(List<Dictionary<int, int>> trace, IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int x = a.Count, y = b.Count;
        var edits = new List<Edit>();

        // 从最后一步开始回溯，只有 d>0 时才访问 trace[d-1]
        for (var d = trace.Count - 1; d > 0; d--)
        {
            var k = x - y;
            var prev = trace[d - 1];

            int prevK;
            int prevX;

            // 选择来源：插入或删除
            if (k == -d || (k != d && Get(prev, k - 1) < Get(prev, k + 1)))
            {
                // 来自下方（插入）
                prevK = k + 1;
                prevX = Get(prev, prevK);
                var prevY = prevX - prevK;

                // 这一步是把 b[prevY] 插入到 a 的路径上
                edits.Add(new Edit(EditKind.Insert, prevX, prevX, prevY, prevY + 1));
                x = prevX;
                y = prevY;
            }
            else
            {
                // 来自右方（删除）
                prevK = k - 1;
                prevX = Get(prev, prevK) + 1;
                var prevY = prevX - prevK;

                // 这一步是删除了 a[prevX-1]
                edits.Add(new Edit(EditKind.Delete, prevX - 1, prevX, prevY, prevY));
                x = prevX - 1;
                y = prevY;
            }

            // 回溯 snake（相等片段）
            while (x > 0 && y > 0 && a[x - 1] == b[y - 1])
            {
                edits.Add(new Edit(EditKind.Equal, x - 1, x, y - 1, y));
                x--;
                y--;
            }
        }

        // 处理起点的相等片段（d==0）
        while (x > 0 && y > 0 && a[x - 1] == b[y - 1])
        {
            edits.Add(new Edit(EditKind.Equal, x - 1, x, y - 1, y));
            x--;
            y--;
        }

        edits.Reverse();
        return Coalesce(edits);

        static int Get(Dictionary<int, int> d, int k) => d.GetValueOrDefault(k, 0);
    }

    // 合并连续的非相等片段为 Replace，压缩连续 Equal
    private static List<Edit> Coalesce(List<Edit> edits)
    {
        var res = new List<Edit>();
        var i = 0;
        while (i < edits.Count)
        {
            var e = edits[i];
            if (e.Kind == EditKind.Equal)
            {
                // 合并连续 Equal
                int aStart = e.AStart, bStart = e.BStart;
                int aEnd = e.AEnd, bEnd = e.BEnd;
                var j = i + 1;
                while (j < edits.Count && edits[j].Kind == EditKind.Equal)
                {
                    aEnd = edits[j].AEnd;
                    bEnd = edits[j].BEnd;
                    j++;
                }
                res.Add(new Edit(EditKind.Equal, aStart, aEnd, bStart, bEnd));
                i = j;
                continue;
            }

            // 聚合直到遇到下一个 Equal
            int @as = e.AStart, ae = e.AEnd, bs = e.BStart, be = e.BEnd;
            var hasDel = e.Kind == EditKind.Delete;
            var hasIns = e.Kind == EditKind.Insert;
            var k = i + 1;
            while (k < edits.Count && edits[k].Kind != EditKind.Equal)
            {
                hasDel |= edits[k].Kind == EditKind.Delete;
                hasIns |= edits[k].Kind == EditKind.Insert;
                ae = edits[k].AEnd;
                be = edits[k].BEnd;
                k++;
            }

            var kind = (hasDel && hasIns) ? EditKind.Replace
                : hasDel ? EditKind.Delete
                : EditKind.Insert;

            res.Add(new Edit(kind, @as, ae, bs, be));
            i = k;
        }
        return res;
    }
}

public static class TextDifferenceBuilder
{
    /// <summary>
    /// A line in the text, with its start offset and length in the original text. Includes line ending.
    /// </summary>
    /// <param name="Start"></param>
    /// <param name="Length"></param>
    /// <param name="Text"></param>
    private readonly record struct Line(int Start, int Length, string Text);

    public static void BuildLineDiff(TextDifference diff, string original, string updated)
    {
        var a = SplitLines(original);
        var b = SplitLines(updated);

        var aLines = a.Select(l => l.Text).ToList();
        var bLines = b.Select(l => l.Text).ToList();

        var edits = new List<MyersDifference.Edit>();
        foreach (var edit in MyersDifference.Diff(aLines, bLines).OrderBy(e => e.AStart))
        {
            // Merge consecutive equal edits
            if (edits.Count > 0 && edit.Kind == edits[^1].Kind)
            {
                var last = edits[^1];
                edits[^1] = new MyersDifference.Edit(
                    last.Kind,
                    last.AStart,
                    edit.AEnd,
                    last.BStart,
                    edit.BEnd);
            }
            else
            {
                edits.Add(edit);
            }
        }

        foreach (var e in edits)
        {
            switch (e.Kind)
            {
                case MyersDifference.EditKind.Equal:
                {
                    break;
                }
                case MyersDifference.EditKind.Delete:
                {
                    var (start, end) = SpanOf(a, e.AStart, e.AEnd);
                    if (end > start)
                        diff.Add(TextChange.Delete(start, end - start));
                    break;
                }
                case MyersDifference.EditKind.Insert:
                {
                    var at = (e.AStart >= a.Count) ? original.Length : a[e.AStart].Start;
                    var newText = Concat(b, e.BStart, e.BEnd);
                    diff.Add(TextChange.Insert(at, newText));
                    break;
                }
                case MyersDifference.EditKind.Replace:
                {
                    var (start, end) = SpanOf(a, e.AStart, e.AEnd);
                    var newText = Concat(b, e.BStart, e.BEnd);
                    diff.Add(TextChange.Replace(start, end - start, newText));
                    break;
                }
            }
        }

        // Validate the constructed diff
        diff.ValidateAgainst(original);
    }

    private static (int start, int end) SpanOf(List<Line> lines, int startIdx, int endIdx)
    {
        if (startIdx >= endIdx) return (lines.Count > startIdx ? lines[startIdx].Start : lines.LastOrDefault().Start, lines.LastOrDefault().Start);
        var start = lines[startIdx].Start;
        var end = (endIdx - 1 >= 0 && endIdx - 1 < lines.Count) ? lines[endIdx - 1].Start + lines[endIdx - 1].Length : start;
        return (start, end);
    }

    private static string Concat(List<Line> lines, int startIdx, int endIdx)
    {
        return startIdx >= endIdx ?
            string.Empty :
            string.Concat(lines.Skip(startIdx).Take(endIdx - startIdx).Select(l => l.Text));
    }

    private static List<Line> SplitLines(string text)
    {
        var result = new List<Line>();
        int i = 0, start = 0;
        while (i < text.Length)
        {
            if (text[i] == '\r' || text[i] == '\n')
            {
                var nlLen = (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
                var len = (i - start) + nlLen;
                result.Add(new Line(start, len, text.Substring(start, len)));
                i += nlLen;
                start = i;
            }
            else i++;
        }
        if (start < text.Length)
            result.Add(new Line(start, text.Length - start, text.Substring(start, text.Length - start)));
        if (text.Length == 0)
            result.Add(new Line(0, 0, string.Empty));
        return result;
    }
}
