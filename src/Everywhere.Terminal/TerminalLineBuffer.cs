using System.Collections;
using System.Text;

namespace Everywhere.Terminal;

public readonly record struct TerminalLine(long Id, string Text, long Revision);

/// <summary>
/// Bounded, line-oriented output buffer for a single terminal run.
/// </summary>
public sealed class TerminalLineBuffer : IReadOnlyList<TerminalLine>
{
    public const int DefaultMaxLines = 2000;
    public const int DefaultPreviewLength = 1024;
    private const int MaxColumns = 8192;

    private readonly Lock _lock = new();
    private readonly List<TerminalLine> _lines = [];
    private readonly StringBuilder _scratch = new();
    private int _updateDepth;
    private bool _hasPendingChange;
    private bool _hasScrolledOff;
    private long _nextLineId;
    private long _nextRevision;
    private long _version;
    private int _cursorLineIndex = -1;
    private int _cursorColumn;

    public TerminalLineBuffer(int maxLines = DefaultMaxLines)
    {
        if (maxLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines), maxLines, "Max lines must be positive.");
        }

        MaxLines = maxLines;
    }

    public event EventHandler? Changed;

    public int MaxLines { get; }

    public long Version
    {
        get
        {
            lock (_lock)
            {
                return _version;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _lines.Count;
            }
        }
    }

    public TerminalLine this[int index]
    {
        get
        {
            lock (_lock)
            {
                return _lines[index];
            }
        }
    }

    public IDisposable BeginUpdate()
    {
        _lock.Enter();
        _updateDepth++;
        return new UpdateScope(this);
    }

    public void Clear()
    {
        using var _ = BeginUpdate();
        _hasScrolledOff = false;

        if (_lines.Count == 0)
        {
            _cursorLineIndex = -1;
            _cursorColumn = 0;
            return;
        }

        _lines.Clear();
        _cursorLineIndex = -1;
        _cursorColumn = 0;
        RecordChange();
    }

    public void ReplaceText(string text)
    {
        using var _ = BeginUpdate();
        Clear();
        Write(text);
    }

    public void Write(char value)
    {
        using var _ = BeginUpdate();
        WriteCore(value);
    }

    public void Write(ReadOnlySpan<char> text)
    {
        using var _ = BeginUpdate();

        var printableRunStart = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var value = text[i];
            if (IsPrintable(value))
            {
                if (printableRunStart < 0)
                {
                    printableRunStart = i;
                }

                continue;
            }

            FlushPrintableRun(text, printableRunStart, i);
            printableRunStart = -1;
            WriteCore(value);
        }

        FlushPrintableRun(text, printableRunStart, text.Length);
    }

    public void Write(string text)
    {
        Write(text.AsSpan());
    }

    public void CarriageReturn()
    {
        lock (_lock)
        {
            EnsureCurrentLine();
            _cursorColumn = 0;
        }
    }

    public void LineFeed()
    {
        using var _ = BeginUpdate();
        EnsureCurrentLine();
        _cursorLineIndex++;
        _cursorColumn = 0;
        EnsureCurrentLine();
    }

    public void Backspace()
    {
        lock (_lock)
        {
            EnsureCurrentLine();
            if (_cursorColumn > 0)
            {
                _cursorColumn--;
            }
        }
    }

    public void Tab()
    {
        lock (_lock)
        {
            EnsureCurrentLine();
            _cursorColumn = Math.Min(MaxColumns, (_cursorColumn / 4 + 1) * 4);
        }
    }

    public void CursorUp(int count = 1)
    {
        lock (_lock)
        {
            EnsureCurrentLine();
            _cursorLineIndex = Math.Max(0, _cursorLineIndex - Math.Max(count, 1));
        }
    }

    public void CursorDown(int count = 1)
    {
        using var _ = BeginUpdate();
        EnsureCurrentLine();
        _cursorLineIndex += Math.Min(Math.Max(count, 1), MaxLines - 1 - _cursorLineIndex);
        EnsureCurrentLine();
    }

    public void CursorForward(int count = 1)
    {
        lock (_lock)
        {
            EnsureCurrentLine();
            _cursorColumn += Math.Min(Math.Max(count, 1), MaxColumns - _cursorColumn);
        }
    }

    public void CursorBackward(int count = 1)
    {
        lock (_lock)
        {
            EnsureCurrentLine();
            _cursorColumn = Math.Max(0, _cursorColumn - Math.Max(count, 1));
        }
    }

    public void CursorPosition(int row = 1, int column = 1)
    {
        using var _ = BeginUpdate();
        _cursorLineIndex = Math.Clamp(row, 1, MaxLines) - 1;
        _cursorColumn = Math.Clamp(column, 1, MaxColumns + 1) - 1;
        EnsureCurrentLine();
    }

    public void CursorHorizontalAbsolute(int column = 1)
    {
        lock (_lock)
        {
            EnsureCurrentLine();
            _cursorColumn = Math.Clamp(column, 1, MaxColumns + 1) - 1;
        }
    }

    public void EraseLine(int mode = 0)
    {
        using var _ = BeginUpdate();
        EnsureCurrentLine();
        var line = _lines[_cursorLineIndex];
        var text = line.Text;
        var nextText = mode switch
        {
            0 => _cursorColumn < text.Length ? text[.._cursorColumn] : text,
            1 => _cursorColumn + 1 < text.Length
                ? new string(' ', _cursorColumn + 1) + text[(_cursorColumn + 1)..]
                : string.Empty,
            2 => string.Empty,
            _ => text,
        };

        ReplaceLine(_cursorLineIndex, nextText);
    }

    public void EraseDisplay(int mode = 0)
    {
        using var _ = BeginUpdate();
        EnsureCurrentLine();

        switch (mode)
        {
            case 0:
                EraseLine();
                RemoveRange(_cursorLineIndex + 1, _lines.Count - _cursorLineIndex - 1);
                break;
            case 1:
                for (var i = 0; i < _cursorLineIndex; i++)
                {
                    ReplaceLine(i, string.Empty);
                }

                EraseLine(1);
                break;
            case 2:
            case 3:
                Clear();
                break;
        }
    }

    public void DeleteChars(int count = 1)
    {
        using var _ = BeginUpdate();
        EnsureCurrentLine();

        count = Math.Max(count, 1);
        var line = _lines[_cursorLineIndex].Text;
        if (_cursorColumn >= line.Length)
        {
            return;
        }

        var removeCount = Math.Min(count, line.Length - _cursorColumn);
        ReplaceLine(_cursorLineIndex, line.Remove(_cursorColumn, removeCount));
    }

    public void EraseChars(int count = 1)
    {
        using var _ = BeginUpdate();
        EnsureCurrentLine();

        count = Math.Max(count, 1);
        var line = _lines[_cursorLineIndex].Text;
        if (_cursorColumn >= line.Length)
        {
            return;
        }

        var replaceCount = Math.Min(count, line.Length - _cursorColumn);
        _scratch.Clear();
        _scratch.Append(line);
        for (var i = 0; i < replaceCount; i++)
        {
            _scratch[_cursorColumn + i] = ' ';
        }

        ReplaceLine(_cursorLineIndex, _scratch.ToString());
    }

    public void InsertChars(int count = 1)
    {
        using var _ = BeginUpdate();
        EnsureCurrentLine();

        count = Math.Max(count, 1);
        var line = _lines[_cursorLineIndex].Text;
        _scratch.Clear();
        _scratch.Append(line);
        while (_scratch.Length < _cursorColumn)
        {
            _scratch.Append(' ');
        }

        count = Math.Min(count, MaxColumns - _cursorColumn);
        _scratch.Insert(_cursorColumn, new string(' ', count));
        if (_scratch.Length > MaxColumns) _scratch.Length = MaxColumns;
        ReplaceLine(_cursorLineIndex, _scratch.ToString());
    }

    public string GetText()
    {
        lock (_lock)
        {
            return GetTextCore();
        }
    }

    /// <summary>
    /// Returns the last <paramref name="maxLength"/> UTF-16 code units of the rendered output
    /// without copying the entire scrollback, prepending an omission marker if earlier text was dropped.
    /// </summary>
    public string GetPreview(int maxLength = DefaultPreviewLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        lock (_lock)
        {
            return GetTextCore(maxLength);
        }
    }

    internal const string EarlierOutputOmittedPrefix = "[earlier output omitted]\n";

    internal (string Text, bool IsOmitted) GetPreviewParts(int maxLength = DefaultPreviewLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        lock (_lock)
        {
            return GetTextPartsCore(maxLength);
        }
    }

    internal static string StripOmissionPrefix(string text, out bool wasOmitted)
    {
        if (text.StartsWith(EarlierOutputOmittedPrefix, StringComparison.Ordinal))
        {
            wasOmitted = true;
            return text[EarlierOutputOmittedPrefix.Length..];
        }

        wasOmitted = false;
        return text;
    }

    /// <summary>
    /// Truncates combined output to the trailing <paramref name="maxLength"/> UTF-16 code units
    /// without splitting surrogate pairs, prepending an omission marker once when truncated.
    /// </summary>
    public static string TruncateTail(
        string text,
        int maxLength = DefaultPreviewLength,
        bool hasEarlierOmission = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        if (string.IsNullOrEmpty(text))
        {
            return hasEarlierOmission ? "[earlier output omitted]" : string.Empty;
        }

        var skip = Math.Max(0, text.Length - maxLength);
        var isOmitted = hasEarlierOmission || skip > 0;
        if (skip > 0 && skip < text.Length && char.IsLowSurrogate(text[skip]))
        {
            skip++;
        }

        return isOmitted ? string.Concat(EarlierOutputOmittedPrefix, text.AsSpan(skip)) : text;
    }

    internal string GetCursorLineText()
    {
        lock (_lock)
        {
            return _cursorLineIndex >= 0 && _cursorLineIndex < _lines.Count
                ? _lines[_cursorLineIndex].Text
                : string.Empty;
        }
    }

    internal string GetLastNonEmptyLine()
    {
        lock (_lock)
        {
            for (var i = _lines.Count - 1; i >= 0; i--)
            {
                var text = _lines[i].Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return string.Empty;
        }
    }

    public List<TerminalLine> CopyLines(int? maxVisibleLines, out long version)
    {
        if (maxVisibleLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxVisibleLines), maxVisibleLines, "Max visible lines must be positive.");
        }

        lock (_lock)
        {
            version = _version;
            if (_lines.Count == 0)
            {
                return [];
            }

            var last = _lines.Count - 1;
            while (last >= 0 && _lines[last].Text.Length == 0)
            {
                last--;
            }

            if (last < 0)
            {
                return [];
            }

            var lineCount = last + 1;
            var visibleCount = Math.Min(lineCount, maxVisibleLines ?? lineCount);
            var startIndex = lineCount - visibleCount;
            var lines = new List<TerminalLine>(visibleCount);
            for (var i = 0; i < visibleCount; i++)
            {
                lines.Add(_lines[startIndex + i]);
            }

            return lines;
        }
    }

    public IEnumerator<TerminalLine> GetEnumerator() => CopyLines(null, out _).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private string GetTextCore(int maxLength = int.MaxValue)
    {
        var (text, isOmitted) = GetTextPartsCore(maxLength);
        if (!isOmitted) return text;
        return string.Concat(EarlierOutputOmittedPrefix, text);
    }

    private (string Text, bool IsOmitted) GetTextPartsCore(int maxLength)
    {
        if (_lines.Count == 0) return (string.Empty, false);

        var last = _lines.Count - 1;
        while (last >= 0 && _lines[last].Text.Length == 0)
        {
            last--;
        }

        if (last < 0) return (string.Empty, false);

        var first = last;
        var length = _lines[last].Text.Length;
        while (first > 0 && length < maxLength)
        {
            length += 1 + _lines[--first].Text.Length;
        }

        var skip = Math.Max(0, length - maxLength);
        var isOmitted = maxLength != int.MaxValue && (_hasScrolledOff || first > 0 || skip > 0);
        if (skip > 0 && skip < _lines[first].Text.Length && char.IsLowSurrogate(_lines[first].Text[skip]))
        {
            skip++;
        }

        if (skip >= _lines[first].Text.Length && first < last)
        {
            first++;
            skip = 0;
        }

        if (first == last && skip == 0)
        {
            return (_lines[first].Text, isOmitted);
        }

        var builder = new StringBuilder();
        builder.Append(_lines[first].Text.AsSpan(skip));
        for (var i = first + 1; i <= last; i++)
        {
            builder.Append('\n').Append(_lines[i].Text);
        }

        return (builder.ToString(), isOmitted);
    }

    private void WriteCore(char value)
    {
        switch (value)
        {
            case '\r':
                CarriageReturn();
                break;
            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                break;
            case '\b':
                Backspace();
                break;
            case '\t':
                Tab();
                break;
            case >= ' ':
                WriteText(stackalloc[] { value });
                break;
        }
    }

    private static bool IsPrintable(char value)
    {
        return value >= ' ' && value != '\t';
    }

    private void FlushPrintableRun(ReadOnlySpan<char> text, int start, int end)
    {
        if (start >= 0 && end > start)
        {
            WriteText(text[start..end]);
        }
    }

    private void EndUpdate()
    {
        EventHandler? changedHandler;
        var changed = false;

        try
        {
            if (--_updateDepth > 0)
            {
                return;
            }

            if (_hasPendingChange)
            {
                _hasPendingChange = false;
                _version++;
                changed = true;
            }

            changedHandler = Changed;
        }
        finally
        {
            _lock.Exit();
        }

        if (changed)
        {
            changedHandler?.Invoke(this, EventArgs.Empty);
        }
    }

    private void WriteText(ReadOnlySpan<char> text)
    {
        // Soft-wrap pathological single-line output so neither the screen nor its preview loses the trailing diagnostic.
        while (!text.IsEmpty)
        {
            var count = Math.Min(text.Length, MaxColumns - _cursorColumn);
            if (count > 0 && _cursorColumn + count == MaxColumns && char.IsHighSurrogate(text[count - 1]))
            {
                count--;
            }

            if (count == 0)
            {
                LineFeed();
                continue;
            }

            WriteCells(text[..count]);
            text = text[count..];
        }
    }

    private void WriteCells(ReadOnlySpan<char> text)
    {
        EnsureCurrentLine();
        var line = _lines[_cursorLineIndex].Text;
        _scratch.Clear();
        _scratch.Append(line);

        while (_scratch.Length < _cursorColumn)
        {
            _scratch.Append(' ');
        }

        foreach (var value in text)
        {
            if (_cursorColumn < _scratch.Length)
            {
                _scratch[_cursorColumn] = value;
            }
            else
            {
                _scratch.Append(value);
            }

            _cursorColumn++;
        }

        ReplaceLine(_cursorLineIndex, _scratch.ToString());
    }

    private void EnsureCurrentLine()
    {
        if (_cursorLineIndex >= 0 && _cursorLineIndex < _lines.Count)
        {
            return;
        }

        if (_cursorLineIndex < 0)
        {
            _cursorLineIndex = 0;
        }

        while (_lines.Count <= _cursorLineIndex)
        {
            AddLine(string.Empty);
        }
    }

    private void AddLine(string text)
    {
        text = NormalizeLineText(text);
        var line = CreateLine(text);
        _lines.Add(line);
        RecordChange();
        TrimToLimit();
    }

    private void ReplaceLine(int index, string text)
    {
        text = NormalizeLineText(text);
        var oldLine = _lines[index];
        if (oldLine.Text == text)
        {
            return;
        }

        var newLine = oldLine with { Text = text, Revision = ++_nextRevision };
        _lines[index] = newLine;
        RecordChange();
    }

    private TerminalLine CreateLine(string text)
    {
        return new TerminalLine(++_nextLineId, text, ++_nextRevision);
    }

    private static string NormalizeLineText(string text)
    {
        return text.TrimEnd(' ');
    }

    private void TrimToLimit()
    {
        if (_lines.Count <= MaxLines) return;
        _hasScrolledOff = true;
        RemoveRange(0, _lines.Count - MaxLines);
    }

    private void RemoveRange(int index, int count)
    {
        if (count <= 0 || index < 0 || index >= _lines.Count)
        {
            return;
        }

        count = Math.Min(count, _lines.Count - index);
        _lines.RemoveRange(index, count);

        if (_lines.Count == 0)
        {
            _cursorLineIndex = -1;
            _cursorColumn = 0;
        }
        else if (_cursorLineIndex >= index + count)
        {
            _cursorLineIndex -= count;
        }
        else if (_cursorLineIndex >= index)
        {
            _cursorLineIndex = Math.Min(index, _lines.Count - 1);
            _cursorColumn = Math.Min(_cursorColumn, _lines[_cursorLineIndex].Text.Length);
        }

        RecordChange();
    }

    private void RecordChange()
    {
        if (_updateDepth > 0)
        {
            _hasPendingChange = true;
            return;
        }

        _version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class UpdateScope(TerminalLineBuffer owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.EndUpdate();
            }
        }
    }
}
