using System.Text;

namespace Everywhere.Chat.Plugins.BuiltIn.Terminal;

/// <summary>
/// A minimal VT100/ECMA-48 sequence parser that drives a <see cref="VirtualTerminalBuffer"/>.
/// Implements the state machine needed to handle ANSI escape sequences from PTY output,
/// including CSI, OSC, and single-character escape sequences.
///
/// This parser is intentionally minimal — it only handles sequences that affect text layout
/// (cursor movement, erase, scroll, etc.). Color/style sequences are consumed but ignored.
/// </summary>
public sealed class VtSequenceParser
{
    private readonly VirtualTerminalBuffer _buffer;

    // UTF-16 surrogate pair tracking
    private char _highSurrogate;

    public VtSequenceParser(VirtualTerminalBuffer buffer)
    {
        _buffer = buffer;
    }

    private enum State
    {
        Ground,
        Escape,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        OscString,
        OscStringEscape, // Inside OSC, saw ESC — waiting for '\' to form ST
        CharsetSelect,
    }

    private State _state = State.Ground;

    // CSI parameter accumulation
    private readonly List<int> _csiParams = [];
    private int _currentParam = -1; // -1 means "no digits accumulated yet"
    private bool _csiPrivateParam; // '?' prefix
    private char _csiIntermediate; // intermediate character (e.g. '!' for DECSTR)

    // OSC string accumulation
    private readonly StringBuilder _oscBuffer = new(64);

    /// <summary>
    /// Feed a chunk of text (from PTY output) to the parser.
    /// </summary>
    public void Feed(ReadOnlySpan<char> input)
    {
        foreach (var c in input)
        {
            FeedChar(c);
        }
    }

    /// <summary>
    /// Feed a single character to the parser state machine.
    /// </summary>
    private void FeedChar(char c)
    {
        // Handle UTF-16 surrogate pairs
        if (char.IsHighSurrogate(c))
        {
            _highSurrogate = c;
            return;
        }
        if (char.IsLowSurrogate(c))
        {
            if (_highSurrogate != 0)
            {
                // Complete surrogate pair — write as two chars
                if (_state == State.Ground)
                {
                    _buffer.WriteChar(_highSurrogate);
                    _buffer.WriteChar(c);
                }
                _highSurrogate = '\0';
                return;
            }
            // Orphan low surrogate, skip
            return;
        }

        // Flush any pending high surrogate
        if (_highSurrogate != 0)
        {
            if (_state == State.Ground)
            {
                _buffer.WriteChar(_highSurrogate);
            }
            _highSurrogate = '\0';
        }

        switch (_state)
        {
            case State.Ground:
                HandleGround(c);
                break;
            case State.Escape:
                HandleEscape(c);
                break;
            case State.CsiEntry:
                HandleCsiEntry(c);
                break;
            case State.CsiParam:
                HandleCsiParam(c);
                break;
            case State.CsiIntermediate:
                HandleCsiIntermediate(c);
                break;
            case State.OscString:
                HandleOscString(c);
                break;
            case State.OscStringEscape:
                HandleOscStringEscape(c);
                break;
            case State.CharsetSelect:
                // Consume the charset designation character and return to ground
                _state = State.Ground;
                break;
        }
    }

    #region State Handlers

    private void HandleGround(char c)
    {
        switch (c)
        {
            case '\x1b': // ESC
                _state = State.Escape;
                break;
            case '\r': // CR
                _buffer.CarriageReturn();
                break;
            case '\n': // LF
            case '\v': // VT
            case '\f': // FF
                _buffer.LineFeed();
                break;
            case '\b': // BS
                _buffer.Backspace();
                break;
            case '\t': // HT
                _buffer.Tab();
                break;
            case '\a': // BEL — ignore
                break;
            case '\0': // NUL — ignore
                break;
            default:
                if (c >= 0x20) // Printable character (including Unicode)
                {
                    _buffer.WriteChar(c);
                }
                break;
        }
    }

    private void HandleEscape(char c)
    {
        switch (c)
        {
            case '[': // CSI
                _state = State.CsiEntry;
                ResetCsi();
                break;
            case ']': // OSC
                _state = State.OscString;
                _oscBuffer.Clear();
                break;
            case '(': // Charset designation G0
            case ')': // Charset designation G1
                _state = State.CharsetSelect;
                break;
            case '7': // DECSC — Save Cursor
                _buffer.SaveCursor();
                _state = State.Ground;
                break;
            case '8': // DECRC — Restore Cursor
                _buffer.RestoreCursor();
                _state = State.Ground;
                break;
            case 'D': // IND — Index (move down, scroll if at bottom)
                _buffer.LineFeed();
                _state = State.Ground;
                break;
            case 'E': // NEL — Next Line
                _buffer.CarriageReturn();
                _buffer.LineFeed();
                _state = State.Ground;
                break;
            case 'M': // RI — Reverse Index (move up, scroll if at top)
                if (_buffer.CursorY <= 0)
                {
                    _buffer.ScrollDown(1);
                }
                else
                {
                    _buffer.CursorUp(1);
                }
                _state = State.Ground;
                break;
            case 'c': // RIS — Full Reset
                _buffer.EraseDisplay(2);
                _buffer.CursorPosition(1, 1);
                _state = State.Ground;
                break;
            case '=': // DECKPAM — Application Keypad Mode
            case '>': // DECKPNM — Normal Keypad Mode
                _state = State.Ground; // Ignore
                break;
            default:
                // Unknown escape sequence — return to ground
                _state = State.Ground;
                break;
        }
    }

    private void HandleCsiEntry(char c)
    {
        if (c >= '0' && c <= '9')
        {
            _currentParam = c - '0';
            _state = State.CsiParam;
        }
        else if (c == '?')
        {
            _csiPrivateParam = true;
            _state = State.CsiParam;
        }
        else if (c == '>' || c == '=')
        {
            // Secondary DA or other — ignore
            _csiPrivateParam = true;
            _state = State.CsiParam;
        }
        else if (c >= 0x40 && c <= 0x7E)
        {
            // Single-character CSI with no params
            DispatchCsi(c);
        }
        else if (c >= 0x20 && c <= 0x2F)
        {
            _csiIntermediate = c;
            _state = State.CsiIntermediate;
        }
        else
        {
            // Invalid — return to ground
            _state = State.Ground;
        }
    }

    private void HandleCsiParam(char c)
    {
        if (c >= '0' && c <= '9')
        {
            if (_currentParam < 0) _currentParam = 0;
            _currentParam = _currentParam * 10 + (c - '0');
        }
        else if (c == ';')
        {
            _csiParams.Add(_currentParam < 0 ? 0 : _currentParam);
            _currentParam = -1;
        }
        else if (c >= 0x40 && c <= 0x7E)
        {
            // Final byte — dispatch
            if (_currentParam >= 0)
            {
                _csiParams.Add(_currentParam);
            }
            DispatchCsi(c);
        }
        else if (c >= 0x20 && c <= 0x2F)
        {
            _csiIntermediate = c;
            _state = State.CsiIntermediate;
        }
        else
        {
            // Invalid — return to ground
            _state = State.Ground;
        }
    }

    private void HandleCsiIntermediate(char c)
    {
        if (c >= 0x40 && c <= 0x7E)
        {
            // Final byte — dispatch (with intermediate)
            if (_currentParam >= 0)
            {
                _csiParams.Add(_currentParam);
            }
            DispatchCsi(c);
        }
        else if (c < 0x20 || c > 0x2F)
        {
            // Invalid — return to ground
            _state = State.Ground;
        }
        // else: more intermediate bytes, stay in this state
    }

    private void HandleOscString(char c)
    {
        switch (c)
        {
            case '\a': // BEL — end of OSC
                ProcessOsc();
                _state = State.Ground;
                break;
            case '\x1b': // Potential ST (ESC \)
                _state = State.OscStringEscape;
                break;
            default:
                _oscBuffer.Append(c);
                break;
        }
    }

    /// <summary>
    /// Inside OSC, we saw ESC. If next char is '\', it's ST (string terminator).
    /// Otherwise, treat the ESC as part of the OSC content and re-enter OscString.
    /// </summary>
    private void HandleOscStringEscape(char c)
    {
        if (c == '\\')
        {
            // ST — String Terminator
            ProcessOsc();
            _state = State.Ground;
        }
        else
        {
            // Not ST — the ESC was part of the content
            _oscBuffer.Append('\x1b');
            _oscBuffer.Append(c);
            _state = State.OscString;
        }
    }

    #endregion

    #region CSI Dispatch

    /// <summary>
    /// Dispatch a CSI sequence to the buffer.
    /// </summary>
    private void DispatchCsi(char finalByte)
    {
        // For private sequences (DECSET/DECRST), we mostly ignore them
        // but we need to handle some that affect layout
        if (_csiPrivateParam)
        {
            DispatchPrivateCsi(finalByte);
        }
        else
        {
            DispatchStandardCsi(finalByte);
        }

        ResetCsi();
        _state = State.Ground;
    }

    private void DispatchStandardCsi(char finalByte)
    {
        var p0 = GetParam(0, 0);
        var p1 = GetParam(1, 0);

        switch (finalByte)
        {
            case 'A': // CUU — Cursor Up
                _buffer.CursorUp(Math.Max(p0, 1));
                break;
            case 'B': // CUD — Cursor Down
                _buffer.CursorDown(Math.Max(p0, 1));
                break;
            case 'C': // CUF — Cursor Forward
                _buffer.CursorForward(Math.Max(p0, 1));
                break;
            case 'D': // CUB — Cursor Backward
                _buffer.CursorBackward(Math.Max(p0, 1));
                break;
            case 'E': // CNL — Cursor Next Line
                _buffer.CursorNextLine(Math.Max(p0, 1));
                break;
            case 'F': // CPL — Cursor Previous Line
                _buffer.CursorPreviousLine(Math.Max(p0, 1));
                break;
            case 'G': // CHA — Cursor Horizontal Absolute
                _buffer.CursorHorizontalAbsolute(Math.Max(p0, 1));
                break;
            case 'H': // CUP — Cursor Position
            case 'f': // HVP — Horizontal Vertical Position
                _buffer.CursorPosition(Math.Max(p0, 1), Math.Max(p1, 1));
                break;
            case 'J': // ED — Erase in Display
                _buffer.EraseDisplay(p0);
                break;
            case 'K': // EL — Erase in Line
                _buffer.EraseLine(p0);
                break;
            case 'L': // IL — Insert Lines
                _buffer.InsertLines(Math.Max(p0, 1));
                break;
            case 'M': // DL — Delete Lines
                _buffer.DeleteLines(Math.Max(p0, 1));
                break;
            case 'P': // DCH — Delete Characters
                _buffer.DeleteChars(Math.Max(p0, 1));
                break;
            case 'S': // SU — Scroll Up
                _buffer.ScrollUp(Math.Max(p0, 1));
                break;
            case 'T': // SD — Scroll Down
                _buffer.ScrollDown(Math.Max(p0, 1));
                break;
            case '@': // ICH — Insert Characters
                _buffer.InsertChars(Math.Max(p0, 1));
                break;
            case 'r': // DECSTBM — Set Scrolling Region
                _buffer.SetScrollRegion(Math.Max(p0, 1), p1 > 0 ? p1 : -1);
                break;
            case 's': // SCP — Save Cursor Position
                _buffer.SaveCursor();
                break;
            case 'u': // RCP — Restore Cursor Position
                _buffer.RestoreCursor();
                break;
            case 'd': // VPA — Vertical Position Absolute
                _buffer.CursorPosition(Math.Max(p0, 1), _buffer.CursorX + 1);
                break;
            case 'X': // ECH — Erase Characters
                if (_buffer.CursorY < 0) break;
                var eraseCount = Math.Max(p0, 1);
                for (var i = 0; i < eraseCount && _buffer.CursorX + i < 1024; i++)
                {
                    // Write space at cursor + i position
                    var savedX = _buffer.CursorX;
                    _buffer.CursorX += i;
                    _buffer.WriteChar(' ');
                    _buffer.CursorX = savedX;
                }
                break;
            case 'm': // SGR — Select Graphic Rendition (colors/styles) — ignore
            case 'n': // DSR — Device Status Report — ignore
            case 'c': // DA — Device Attributes — ignore
            case 'q': // DECSCUSR — Set Cursor Style — ignore
            case 'x': // DECREQTPARM — ignore
            case 'g': // TBC — Tab Clear — ignore
            case 'Z': // CBT — Cursor Backward Tabulation — ignore
            case 'I': // CHT — Cursor Horizontal Tabulation — ignore
            case 'b': // REP — Repeat — ignore
            case 'z': // DECERA — ignore
            case '{': // DECSERA — ignore
            case 'p': // various — ignore
            case 't': // Window manipulation — ignore
                break;
            default:
                // Unknown CSI — ignore
                break;
        }
    }

    private void DispatchPrivateCsi(char finalByte)
    {
        // Private sequences (prefixed with '?')
        // Most are DECSET/DECRST for terminal modes
        var p0 = GetParam(0, 0);

        switch (finalByte)
        {
            case 'h': // DECSET — Set Mode
            case 'l': // DECRST — Reset Mode
                // Common modes we might want to track:
                // ?25: cursor visibility
                // ?1049: alternate screen buffer
                // ?2004: bracketed paste mode
                // ?9001: Windows Terminal proprietary
                // All ignored for text extraction
                break;
            case 'J': // DECSED — Selective Erase in Display
                _buffer.EraseDisplay(p0);
                break;
            case 'K': // DECSEL — Selective Erase in Line
                _buffer.EraseLine(p0);
                break;
            case 'm': // SGR with private params — ignore (colors)
                break;
            case 'n': // DSR — Device Status Report — ignore
                break;
            default:
                // Unknown private CSI — ignore
                break;
        }
    }

    #endregion

    #region OSC Processing

    private void ProcessOsc()
    {
        // OSC sequences are typically:
        // OSC 0 ; <title> ST  — Set window title
        // OSC 2 ; <title> ST  — Set window title
        // OSC 4 ; <num> ; <color> ST  — Set palette color
        // etc.
        //
        // We ignore all OSC sequences for text extraction.
        // The window title might be useful in the future but not for now.
    }

    #endregion

    #region Helpers

    private void ResetCsi()
    {
        _csiParams.Clear();
        _currentParam = -1;
        _csiPrivateParam = false;
        _csiIntermediate = '\0';
    }

    /// <summary>
    /// Get the n-th CSI parameter (0-based), with a default value if not provided.
    /// CSI parameters are 1-based in the spec, but 0 means "use default".
    /// </summary>
    private int GetParam(int index, int defaultValue)
    {
        if (index < _csiParams.Count)
        {
            var val = _csiParams[index];
            return val > 0 ? val : defaultValue;
        }
        return defaultValue;
    }

    #endregion
}
