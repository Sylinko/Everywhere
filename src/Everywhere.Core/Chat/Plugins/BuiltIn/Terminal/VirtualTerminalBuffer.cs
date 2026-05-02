using System.Text;

namespace Everywhere.Chat.Plugins.BuiltIn.Terminal;

/// <summary>
/// A virtual terminal buffer that maintains a 2D character grid with cursor tracking.
/// Similar to xterm.js screen buffer but simplified for text extraction only (no styling).
/// Height grows on demand to capture all output.
/// </summary>
public sealed class VirtualTerminalBuffer
{
    private readonly int _cols;
    private readonly List<char[]> _lines = [];
    private int _scrollTop;
    private int _scrollBottom = -1; // -1 means "last line"

    // Cursor state
    public int CursorX { get; set; }
    public int CursorY { get; set; }

    // Saved cursor state (CSI s / CSI u)
    private int _savedCursorX;
    private int _savedCursorY;

    /// <summary>
    /// The effective scroll bottom (resolves -1 to the last line index).
    /// </summary>
    private int ScrollBottom => _scrollBottom >= 0 ? _scrollBottom : MaxRow;

    /// <summary>
    /// The maximum row index that has been written to.
    /// </summary>
    private int MaxRow => _lines.Count - 1;

    public VirtualTerminalBuffer(int cols)
    {
        _cols = cols;
    }

    /// <summary>
    /// Ensure the buffer has at least (row + 1) lines.
    /// </summary>
    private void EnsureLine(int row)
    {
        while (_lines.Count <= row)
        {
            _lines.Add(new char[_cols]); // initialized to '\0'
        }
    }

    /// <summary>
    /// Write a single character at the current cursor position and advance the cursor.
    /// Handles line wrapping.
    /// </summary>
    public void WriteChar(char c)
    {
        EnsureLine(CursorY);

        if (CursorX >= _cols)
        {
            // Line wrap: move to next line
            CursorX = 0;
            CursorY++;
            EnsureLine(CursorY);
        }

        _lines[CursorY][CursorX] = c;
        CursorX++;
    }

    /// <summary>
    /// Write a string at the current cursor position.
    /// </summary>
    public void WriteString(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            WriteChar(c);
        }
    }

    /// <summary>
    /// Carriage return: move cursor to column 0.
    /// </summary>
    public void CarriageReturn()
    {
        CursorX = 0;
    }

    /// <summary>
    /// Line feed: move cursor down one line. If at scroll bottom, scroll up.
    /// </summary>
    public void LineFeed()
    {
        if (CursorY >= ScrollBottom)
        {
            ScrollUp(1);
        }
        else
        {
            CursorY++;
        }
        EnsureLine(CursorY);
    }

    /// <summary>
    /// Backspace: move cursor left one column (minimum 0).
    /// </summary>
    public void Backspace()
    {
        if (CursorX > 0)
        {
            CursorX--;
        }
    }

    /// <summary>
    /// Tab: advance cursor to the next tab stop (every 8 columns).
    /// </summary>
    public void Tab()
    {
        CursorX = Math.Min((CursorX / 8 + 1) * 8, _cols - 1);
    }

    #region Cursor Movement (CSI sequences)

    /// <summary>
    /// CSI A: Cursor Up.
    /// </summary>
    public void CursorUp(int n = 1)
    {
        CursorY = Math.Max(CursorY - n, _scrollTop);
    }

    /// <summary>
    /// CSI B: Cursor Down.
    /// </summary>
    public void CursorDown(int n = 1)
    {
        CursorY = Math.Min(CursorY + n, ScrollBottom);
        EnsureLine(CursorY);
    }

    /// <summary>
    /// CSI C: Cursor Forward.
    /// </summary>
    public void CursorForward(int n = 1)
    {
        CursorX = Math.Min(CursorX + n, _cols - 1);
    }

    /// <summary>
    /// CSI D: Cursor Backward.
    /// </summary>
    public void CursorBackward(int n = 1)
    {
        CursorX = Math.Max(CursorX - n, 0);
    }

    /// <summary>
    /// CSI E: Cursor Next Line (move to beginning of line n lines down).
    /// </summary>
    public void CursorNextLine(int n = 1)
    {
        CursorDown(n);
        CursorX = 0;
    }

    /// <summary>
    /// CSI F: Cursor Previous Line (move to beginning of line n lines up).
    /// </summary>
    public void CursorPreviousLine(int n = 1)
    {
        CursorUp(n);
        CursorX = 0;
    }

    /// <summary>
    /// CSI G: Cursor Horizontal Absolute (move to column n, 1-based).
    /// </summary>
    public void CursorHorizontalAbsolute(int n = 1)
    {
        CursorX = Math.Clamp(n - 1, 0, _cols - 1);
    }

    /// <summary>
    /// CSI H / CSI f: Cursor Position (row, col), 1-based.
    /// </summary>
    public void CursorPosition(int row = 1, int col = 1)
    {
        // CSI H is 1-based; convert to 0-based
        CursorY = Math.Clamp(row - 1, 0, int.MaxValue);
        CursorX = Math.Clamp(col - 1, 0, _cols - 1);
        EnsureLine(CursorY);
    }

    /// <summary>
    /// CSI s: Save Cursor Position.
    /// </summary>
    public void SaveCursor()
    {
        _savedCursorX = CursorX;
        _savedCursorY = CursorY;
    }

    /// <summary>
    /// CSI u: Restore Cursor Position.
    /// </summary>
    public void RestoreCursor()
    {
        CursorX = _savedCursorX;
        CursorY = _savedCursorY;
        EnsureLine(CursorY);
    }

    #endregion

    #region Erase Operations

    /// <summary>
    /// CSI J: Erase in Display.
    /// 0 = erase from cursor to end of screen
    /// 1 = erase from start of screen to cursor
    /// 2 = erase entire screen
    /// 3 = erase scrollback buffer
    /// </summary>
    public void EraseDisplay(int mode = 0)
    {
        switch (mode)
        {
            case 0: // cursor to end
                EraseLine(0); // erase rest of current line
                for (var y = CursorY + 1; y < _lines.Count; y++)
                {
                    Array.Clear(_lines[y]);
                }
                break;

            case 1: // start to cursor
                for (var y = 0; y < CursorY; y++)
                {
                    if (y < _lines.Count) Array.Clear(_lines[y]);
                }
                EraseLine(1); // erase start of current line
                break;

            case 2: // entire screen
            case 3: // entire screen + scrollback
                foreach (var line in _lines)
                {
                    Array.Clear(line);
                }
                break;
        }
    }

    /// <summary>
    /// CSI K: Erase in Line.
    /// 0 = erase from cursor to end of line
    /// 1 = erase from start of line to cursor
    /// 2 = erase entire line
    /// </summary>
    public void EraseLine(int mode = 0)
    {
        if (CursorY >= _lines.Count) return;
        var line = _lines[CursorY];

        switch (mode)
        {
            case 0: // cursor to end
                Array.Clear(line, CursorX, _cols - CursorX);
                break;

            case 1: // start to cursor
                Array.Clear(line, 0, CursorX + 1);
                break;

            case 2: // entire line
                Array.Clear(line);
                break;
        }
    }

    #endregion

    #region Insert/Delete Operations

    /// <summary>
    /// CSI @: Insert n blank characters at cursor position.
    /// </summary>
    public void InsertChars(int n = 1)
    {
        if (CursorY >= _lines.Count) return;
        var line = _lines[CursorY];
        n = Math.Min(n, _cols - CursorX);

        // Shift characters right
        Array.Copy(line, CursorX, line, CursorX + n, _cols - CursorX - n);
        // Clear inserted area
        Array.Clear(line, CursorX, n);
    }

    /// <summary>
    /// CSI P: Delete n characters at cursor position.
    /// </summary>
    public void DeleteChars(int n = 1)
    {
        if (CursorY >= _lines.Count) return;
        var line = _lines[CursorY];
        n = Math.Min(n, _cols - CursorX);

        // Shift characters left
        Array.Copy(line, CursorX + n, line, CursorX, _cols - CursorX - n);
        // Clear vacated area
        Array.Clear(line, _cols - n, n);
    }

    /// <summary>
    /// CSI L: Insert n blank lines at cursor position. Lines below scroll down.
    /// </summary>
    public void InsertLines(int n = 1)
    {
        if (CursorY < _scrollTop || CursorY > ScrollBottom) return;
        n = Math.Min(n, ScrollBottom - CursorY + 1);

        // Move lines down within scroll region
        for (var i = ScrollBottom; i >= CursorY + n; i--)
        {
            EnsureLine(i);
            EnsureLine(i - n);
            Array.Copy(_lines[i - n], _lines[i], _cols);
        }

        // Clear inserted lines
        for (var i = CursorY; i < CursorY + n && i <= ScrollBottom; i++)
        {
            EnsureLine(i);
            Array.Clear(_lines[i]);
        }
    }

    /// <summary>
    /// CSI M: Delete n lines at cursor position. Lines below scroll up.
    /// </summary>
    public void DeleteLines(int n = 1)
    {
        if (CursorY < _scrollTop || CursorY > ScrollBottom) return;
        n = Math.Min(n, ScrollBottom - CursorY + 1);

        // Move lines up within scroll region
        for (var i = CursorY; i <= ScrollBottom - n; i++)
        {
            EnsureLine(i);
            EnsureLine(i + n);
            Array.Copy(_lines[i + n], _lines[i], _cols);
        }

        // Clear vacated lines at bottom of scroll region
        for (var i = ScrollBottom - n + 1; i <= ScrollBottom; i++)
        {
            EnsureLine(i);
            Array.Clear(_lines[i]);
        }
    }

    #endregion

    #region Scroll Operations

    /// <summary>
    /// CSI S: Scroll Up — entire screen content moves up, new blank line at bottom.
    /// </summary>
    public void ScrollUp(int n = 1)
    {
        n = Math.Min(n, ScrollBottom - _scrollTop + 1);

        // Move lines up within scroll region
        for (var i = _scrollTop; i <= ScrollBottom - n; i++)
        {
            EnsureLine(i);
            EnsureLine(i + n);
            Array.Copy(_lines[i + n], _lines[i], _cols);
        }

        // Clear new lines at bottom
        for (var i = ScrollBottom - n + 1; i <= ScrollBottom; i++)
        {
            EnsureLine(i);
            Array.Clear(_lines[i]);
        }
    }

    /// <summary>
    /// CSI T: Scroll Down — entire screen content moves down, new blank line at top.
    /// </summary>
    public void ScrollDown(int n = 1)
    {
        n = Math.Min(n, ScrollBottom - _scrollTop + 1);

        // Move lines down within scroll region
        for (var i = ScrollBottom; i >= _scrollTop + n; i--)
        {
            EnsureLine(i);
            EnsureLine(i - n);
            Array.Copy(_lines[i - n], _lines[i], _cols);
        }

        // Clear new lines at top
        for (var i = _scrollTop; i < _scrollTop + n; i++)
        {
            EnsureLine(i);
            Array.Clear(_lines[i]);
        }
    }

    /// <summary>
    /// CSI r: Set Scrolling Region (top, bottom), 1-based.
    /// </summary>
    public void SetScrollRegion(int top = 1, int bottom = -1)
    {
        _scrollTop = Math.Max(top - 1, 0);
        _scrollBottom = bottom > 0 ? bottom - 1 : -1; // -1 means "last line"
        // Reset cursor to home when scroll region changes
        CursorX = 0;
        CursorY = 0;
    }

    #endregion

    #region Text Extraction

    /// <summary>
    /// Extract all non-empty lines from the buffer as plain text.
    /// Trailing empty lines are trimmed.
    /// </summary>
    public string GetText()
    {
        if (_lines.Count == 0) return string.Empty;

        // Find last non-empty line
        var lastNonEmpty = -1;
        for (var y = _lines.Count - 1; y >= 0; y--)
        {
            if (LineHasContent(y))
            {
                lastNonEmpty = y;
                break;
            }
        }

        if (lastNonEmpty < 0) return string.Empty;

        var sb = new StringBuilder();
        for (var y = 0; y <= lastNonEmpty; y++)
        {
            if (y > 0) sb.Append('\n');
            sb.Append(GetLineText(y));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Check if a line has any non-null, non-space content.
    /// </summary>
    private bool LineHasContent(int row)
    {
        if (row >= _lines.Count) return false;
        var line = _lines[row];
        for (var x = 0; x < _cols; x++)
        {
            if (line[x] != '\0' && line[x] != ' ') return true;
        }
        return false;
    }

    /// <summary>
    /// Get the text content of a single line, trimming trailing whitespace/nulls.
    /// </summary>
    private string GetLineText(int row)
    {
        if (row >= _lines.Count) return string.Empty;
        var line = _lines[row];

        // Find last non-null character
        var end = _cols - 1;
        while (end >= 0 && (line[end] == '\0' || line[end] == ' '))
        {
            end--;
        }

        if (end < 0) return string.Empty;

        // Build string from 0..end, replacing nulls with spaces
        var sb = new StringBuilder(end + 1);
        for (var x = 0; x <= end; x++)
        {
            sb.Append(line[x] == '\0' ? ' ' : line[x]);
        }

        return sb.ToString();
    }

    #endregion
}
