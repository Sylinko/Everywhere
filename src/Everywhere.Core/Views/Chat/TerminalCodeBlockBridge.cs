using Avalonia.Controls.Documents;
using Avalonia.Threading;
using Everywhere.Terminal;
using LiveMarkdown.Avalonia;
using ZLinq;

namespace Everywhere.Views;

internal sealed class TerminalCodeBlockBridge : IDisposable
{
    private readonly TerminalRun _run;
    private readonly TerminalLineBuffer _buffer;
    private readonly CodeBlock _codeBlock;
    private readonly int _maxVisibleLines;
    private readonly List<LineSlot> _slots = [];
    private readonly Lock _gate = new();

    private bool _dirty;
    private bool _disposed;
    private bool _finalApplied;
    private bool _scheduled;
    private int _generation;
    private long _lastAppliedVersion = -1;

    public TerminalCodeBlockBridge(
        TerminalRun run,
        CodeBlock codeBlock,
        int maxVisibleLines)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(codeBlock);
        if (maxVisibleLines <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxVisibleLines),
                maxVisibleLines,
                "Max visible lines must be positive.");
        }

        _run = run;
        _buffer = run.Output;
        _codeBlock = codeBlock;
        _maxVisibleLines = maxVisibleLines;

        _buffer.Changed += HandleBufferChanged;
        _run.Completion.ContinueWith(
            static (_, state) => ((TerminalCodeBlockBridge)state!).HandleRunCompleted(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        lock (_gate)
        {
            _dirty = true;
            ScheduleRefreshLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            _buffer.Changed -= HandleBufferChanged;
        }
    }

    private void HandleBufferChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _dirty = true;
            ScheduleRefreshLocked();
        }
    }

    private void HandleRunCompleted()
    {
        lock (_gate)
        {
            _dirty = true;
            ScheduleRefreshLocked();
        }
    }

    private void ScheduleRefreshLocked()
    {
        if (_disposed || _finalApplied || _scheduled)
        {
            return;
        }

        _scheduled = true;
        var generation = _generation;
        Dispatcher.UIThread.Post(() => Flush(generation), DispatcherPriority.Background);
    }

    private void Flush(int generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }

            _scheduled = false;
            _dirty = false;
        }

        var changed = Synchronize(out var appliedVersion);
        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }

            _lastAppliedVersion = appliedVersion;
        }

        if (changed)
        {
            _codeBlock.HighlightSyntax();
        }

        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }

            if (_dirty || _buffer.Version != _lastAppliedVersion)
            {
                _dirty = true;
                ScheduleRefreshLocked();
                return;
            }

            if (_run.Completion.IsCompleted)
            {
                _finalApplied = true;
                _buffer.Changed -= HandleBufferChanged;
            }
        }
    }

    private bool Synchronize(out long appliedVersion)
    {
        var visibleLines = _buffer.CopyLines(_maxVisibleLines, out appliedVersion);
        var inlines = _codeBlock.Inlines;
        if (visibleLines.Count == 0)
        {
            if (_slots.Count == 0 && inlines.Count == 0)
            {
                return false;
            }

            _slots.Clear();
            inlines.Clear();
            return true;
        }

        if (_slots.Count == 0)
        {
            Rebuild(visibleLines);
            return true;
        }

        if (!TrySynchronizeWithOverlap(visibleLines))
        {
            Rebuild(visibleLines);
            return true;
        }

        var changed = false;
        for (var i = 0; i < visibleLines.Count; i++)
        {
            var line = visibleLines[i];
            var slot = _slots[i];
            if (slot.Id != line.Id)
            {
                Rebuild(visibleLines);
                return true;
            }

            if (slot.Revision == line.Revision)
            {
                continue;
            }

            ReplaceLineInline(i, line);
            changed = true;
        }

        return changed;
    }

    private bool TrySynchronizeWithOverlap(List<TerminalLine> visibleLines)
    {
        var overlap = FindBestOverlap(visibleLines);
        if (overlap.Count == 0)
        {
            return false;
        }

        for (var i = _slots.Count - 1; i >= overlap.OldStart + overlap.Count; i--)
        {
            RemoveLineSlot(i);
        }

        for (var i = overlap.OldStart - 1; i >= 0; i--)
        {
            RemoveLineSlot(i);
        }

        for (var i = 0; i < overlap.NewStart; i++)
        {
            InsertLineSlot(i, visibleLines[i]);
        }

        for (var i = overlap.NewStart + overlap.Count; i < visibleLines.Count; i++)
        {
            InsertLineSlot(_slots.Count, visibleLines[i]);
        }

        return _slots.Count == visibleLines.Count;
    }

    private Overlap FindBestOverlap(List<TerminalLine> visibleLines)
    {
        var oldIndices = new Dictionary<long, int>(_slots.Count);
        for (var i = 0; i < _slots.Count; i++)
        {
            oldIndices[_slots[i].Id] = i;
        }

        var byDelta = new Dictionary<int, OverlapBuilder>();
        for (var newIndex = 0; newIndex < visibleLines.Count; newIndex++)
        {
            if (!oldIndices.TryGetValue(visibleLines[newIndex].Id, out var oldIndex))
            {
                continue;
            }

            var delta = oldIndex - newIndex;
            if (byDelta.TryGetValue(delta, out var builder))
            {
                byDelta[delta] = builder with { Count = builder.Count + 1 };
            }
            else
            {
                byDelta[delta] = new OverlapBuilder(oldIndex, newIndex, 1);
            }
        }

        var best = new Overlap();
        foreach (var builder in byDelta.Values.Where(builder => builder.Count > best.Count))
        {
            best = new Overlap(builder.OldStart, builder.NewStart, builder.Count);
        }

        return best;
    }

    private void Rebuild(List<TerminalLine> visibleLines)
    {
        _slots.Clear();
        _codeBlock.Inlines.Clear();
        foreach (var line in visibleLines.AsValueEnumerable())
        {
            InsertLineSlot(_slots.Count, line);
        }
    }

    private void InsertLineSlot(int slotIndex, TerminalLine line)
    {
        var inlines = _codeBlock.Inlines;
        var slot = new LineSlot(line.Id, line.Revision);
        var run = new Run(line.Text);
        if (_slots.Count == 0)
        {
            inlines.Add(run);
        }
        else if (slotIndex >= _slots.Count)
        {
            inlines.Add(new LineBreak());
            inlines.Add(run);
        }
        else
        {
            var inlineIndex = slotIndex * 2;
            inlines.Insert(inlineIndex, run);
            inlines.Insert(inlineIndex + 1, new LineBreak());
        }

        _slots.Insert(slotIndex, slot);
    }

    private void RemoveLineSlot(int slotIndex)
    {
        var inlines = _codeBlock.Inlines;
        if (_slots.Count == 1)
        {
            inlines.RemoveAt(0);
        }
        else if (slotIndex == _slots.Count - 1)
        {
            inlines.RemoveAt(slotIndex * 2);
            inlines.RemoveAt(slotIndex * 2 - 1);
        }
        else
        {
            var inlineIndex = slotIndex * 2;
            inlines.RemoveAt(inlineIndex);
            inlines.RemoveAt(inlineIndex);
        }

        _slots.RemoveAt(slotIndex);
    }

    private void ReplaceLineInline(int slotIndex, TerminalLine line)
    {
        _codeBlock.Inlines[slotIndex * 2] = new Run(line.Text);
        _slots[slotIndex] = new LineSlot(line.Id, line.Revision);
    }

    private readonly record struct LineSlot(long Id, long Revision);

    private readonly record struct Overlap(int OldStart, int NewStart, int Count);

    private readonly record struct OverlapBuilder(int OldStart, int NewStart, int Count);
}
