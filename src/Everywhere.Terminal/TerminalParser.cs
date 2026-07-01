using System.Text;

namespace Everywhere.Terminal;

public delegate void ShellIntegrationMarkerHandler(in ShellIntegrationMarker marker);

public delegate void TerminalResponseHandler(string response);

/// <summary>
/// A minimal VT100/ECMA-48 sequence parser that tracks terminal cursor state.
/// Implements the state machine needed to handle ANSI escape sequences from PTY output,
/// including CSI, OSC, and single-character escape sequences.
///
/// This parser is intentionally minimal — it only handles sequences that affect text layout
/// (cursor movement, erase, scroll, etc.). Color/style sequences are consumed but ignored.
/// </summary>
public sealed class TerminalParser(
    ShellIntegrationMarkerHandler? shellIntegrationMarkerHandler = null,
    TerminalResponseHandler? terminalResponseHandler = null,
    TerminalDimensions? dimensions = null)
{
    public event ShellIntegrationMarkerHandler? ShellIntegrationMarkerReceived
    {
        add => _shellIntegrationMarkerHandler += value;
        remove => _shellIntegrationMarkerHandler -= value;
    }

    public event TerminalResponseHandler? TerminalResponseRequested
    {
        add => _terminalResponseHandler += value;
        remove => _terminalResponseHandler -= value;
    }

    /// <summary>
    /// Whether any Shell Integration (OSC 633) markers have been detected.
    /// </summary>
    public bool HasDetectedShellIntegration { get; private set; }

    /// <summary>
    /// Whether the shell has requested focus in/out reports via DECSET ?1004.
    /// </summary>
    public bool IsFocusEventTrackingEnabled { get; private set; }

    /// <summary>
    /// Whether the shell has requested bracketed paste via DECSET ?2004.
    /// </summary>
    public bool IsBracketedPasteModeEnabled { get; private set; }

    /// <summary>
    /// Whether the shell has requested Windows Terminal's Win32 input mode via DECSET ?9001.
    /// </summary>
    public bool IsWin32InputModeEnabled { get; private set; }

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
    private char _csiPrefix; // '?', '>', '=' or '\0'
    // private char _csiIntermediate; // intermediate character (e.g. '!' for DECSTR)

    // OSC string accumulation
    private readonly StringBuilder _oscBuffer = new(64);

    // Current terminal dimensions for handling cursor position reports and bounds checking.
    private TerminalDimensions _dimensions = dimensions ?? TerminalDimensions.Default;
    private int _scrollTop;
    private int _scrollBottom = -1;
    private int _savedCursorX;
    private int _savedCursorY;

    // UTF-16 surrogate pair tracking
    private char _highSurrogate;

    private ShellIntegrationMarkerHandler? _shellIntegrationMarkerHandler = shellIntegrationMarkerHandler;
    private TerminalResponseHandler? _terminalResponseHandler = terminalResponseHandler;
    private TerminalLineBuffer? _captureBuffer;
    private IDisposable? _captureUpdateScope;
    private int _captureOriginY;
    private int _feedDepth;

    private int CursorX { get; set; }

    private int CursorY { get; set; }

    private int ScrollBottom => _scrollBottom >= 0 ? _scrollBottom : int.MaxValue;

    public void BeginCapture(TerminalLineBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (!ReferenceEquals(_captureBuffer, output))
        {
            EndCaptureBatch();
            _captureBuffer = output;
            _captureOriginY = CursorY;
        }

        if (_feedDepth > 0)
        {
            BeginCaptureBatch();
        }
    }

    public void EndCapture()
    {
        EndCaptureBatch();
        _captureBuffer = null;
    }

    /// <summary>
    /// Feed a chunk of text (from PTY output) to the parser.
    /// </summary>
    public void Feed(ReadOnlySpan<char> input)
    {
        if (input.IsEmpty) return;

        _feedDepth++;
        if (_captureBuffer is not null)
        {
            BeginCaptureBatch();
        }

        try
        {
            foreach (var c in input)
            {
                FeedChar(c);
            }
        }
        finally
        {
            _feedDepth--;
            if (_feedDepth == 0)
            {
                EndCaptureBatch();
            }
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
                    WriteChar(_highSurrogate);
                    CaptureWrite(_highSurrogate);
                    WriteChar(c);
                    CaptureWrite(c);
                }
                else if (_state == State.OscString)
                {
                    _oscBuffer.Append(_highSurrogate);
                    _oscBuffer.Append(c);
                }
                _highSurrogate = '\0';
            }

            // Orphan low surrogate, skip
            return;
        }

        // Flush any pending high surrogate
        if (_highSurrogate != 0)
        {
            if (_state == State.Ground)
            {
                WriteChar(_highSurrogate);
                CaptureWrite(_highSurrogate);
            }
            else if (_state == State.OscString)
            {
                _oscBuffer.Append(_highSurrogate);
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
            case '\e': // ESC
                _state = State.Escape;
                break;
            case '\r': // CR
                CarriageReturn();
                CaptureCarriageReturn();
                break;
            case '\n': // LF
            case '\v': // VT
            case '\f': // FF
                LineFeed();
                CaptureLineFeed();
                break;
            case '\b': // BS
                Backspace();
                CaptureBackspace();
                break;
            case '\t': // HT
                Tab();
                CaptureTab();
                break;
            case '\a': // BEL — ignore
                break;
            case '\0': // NUL — ignore
                break;
            default:
                if (c >= 0x20) // Printable character (including Unicode)
                {
                    WriteChar(c);
                    CaptureWrite(c);
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
                SaveCursor();
                _state = State.Ground;
                break;
            case '8': // DECRC — Restore Cursor
                RestoreCursor();
                CaptureCursorPositionFromScreen();
                _state = State.Ground;
                break;
            case 'D': // IND — Index (move down, scroll if at bottom)
                LineFeed();
                CaptureLineFeed();
                _state = State.Ground;
                break;
            case 'E': // NEL — Next Line
                CarriageReturn();
                CaptureCarriageReturn();
                LineFeed();
                CaptureLineFeed();
                _state = State.Ground;
                break;
            case 'M': // RI — Reverse Index (move up, scroll if at top)
                if (CursorY > 0)
                {
                    CursorUp();
                    CaptureCursorUp();
                }
                _state = State.Ground;
                break;
            case 'c': // RIS — Full Reset
                CursorPosition();
                CaptureClear();
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
        switch (c)
        {
            case >= '0' and <= '9':
                _currentParam = c - '0';
                _state = State.CsiParam;
                break;
            case '?':
                _csiPrivateParam = true;
                _csiPrefix = c;
                _state = State.CsiParam;
                break;
            case '>' or '=':
                // Secondary DA or other prefixed CSI
                _csiPrivateParam = true;
                _csiPrefix = c;
                _state = State.CsiParam;
                break;
            default:
            {
                if (c >= 0x40 && c <= 0x7E)
                {
                    // Single-character CSI with no params
                    DispatchCsi(c);
                }
                else if (c >= 0x20 && c <= 0x2F)
                {
                    // _csiIntermediate = c;
                    _state = State.CsiIntermediate;
                }
                else
                {
                    // Invalid — return to ground
                    _state = State.Ground;
                }
                break;
            }
        }
    }

    private void HandleCsiParam(char c)
    {
        switch (c)
        {
            case >= '0' and <= '9':
            {
                if (_currentParam < 0) _currentParam = 0;
                _currentParam = _currentParam * 10 + (c - '0');
                break;
            }
            case ';':
                _csiParams.Add(_currentParam < 0 ? 0 : _currentParam);
                _currentParam = -1;
                break;
            default:
            {
                if (c >= 0x40 && c <= 0x7E)
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
                    // _csiIntermediate = c;
                    _state = State.CsiIntermediate;
                }
                else
                {
                    // Invalid — return to ground
                    _state = State.Ground;
                }
                break;
            }
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
            case '\e': // Potential ST (ESC \)
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
            _oscBuffer.Append('\e');
            _oscBuffer.Append(c);
            _state = State.OscString;
        }
    }

    #endregion

    #region CSI Dispatch

    /// <summary>
    /// Dispatch a CSI sequence to the 
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
                CursorUp(Math.Max(p0, 1));
                CaptureCursorUp(Math.Max(p0, 1));
                break;
            case 'B': // CUD — Cursor Down
                CursorDown(Math.Max(p0, 1));
                CaptureCursorDown(Math.Max(p0, 1));
                break;
            case 'C': // CUF — Cursor Forward
                CursorForward(Math.Max(p0, 1));
                CaptureCursorForward(Math.Max(p0, 1));
                break;
            case 'D': // CUB — Cursor Backward
                CursorBackward(Math.Max(p0, 1));
                CaptureCursorBackward(Math.Max(p0, 1));
                break;
            case 'E': // CNL — Cursor Next Line
                CursorNextLine(Math.Max(p0, 1));
                CaptureCursorDown(Math.Max(p0, 1));
                CaptureCarriageReturn();
                break;
            case 'F': // CPL — Cursor Previous Line
                CursorPreviousLine(Math.Max(p0, 1));
                CaptureCursorUp(Math.Max(p0, 1));
                CaptureCarriageReturn();
                break;
            case 'G': // CHA — Cursor Horizontal Absolute
                CursorHorizontalAbsolute(Math.Max(p0, 1));
                CaptureCursorHorizontalAbsoluteFromScreen();
                break;
            case 'H': // CUP — Cursor Position
            case 'f': // HVP — Horizontal Vertical Position
                CursorPosition(Math.Max(p0, 1), Math.Max(p1, 1));
                CaptureCursorPositionFromScreen();
                break;
            case 'J': // ED — Erase in Display
                CaptureEraseDisplay(p0);
                break;
            case 'K': // EL — Erase in Line
                CaptureEraseLine(p0);
                break;
            case 'P': // DCH — Delete Characters
                CaptureDeleteChars(Math.Max(p0, 1));
                break;
            case '@': // ICH — Insert Characters
                CaptureInsertChars(Math.Max(p0, 1));
                break;
            case 'r': // DECSTBM — Set Scrolling Region
                SetScrollRegion(Math.Max(p0, 1), p1 > 0 ? p1 : -1);
                break;
            case 's': // SCP — Save Cursor Position
                SaveCursor();
                break;
            case 'u': // RCP — Restore Cursor Position
                RestoreCursor();
                CaptureCursorPositionFromScreen();
                break;
            case 'd': // VPA — Vertical Position Absolute
                CursorPosition(Math.Max(p0, 1), CursorX + 1);
                CaptureCursorPositionFromScreen();
                break;
            case 'X': // ECH — Erase Characters
                if (CursorY < 0) break;
                var eraseCount = Math.Max(p0, 1);
                for (var i = 0; i < eraseCount && CursorX + i < _dimensions.Columns; i++)
                {
                    // Write space at cursor + i position
                    var savedX = CursorX;
                    CursorX += i;
                    WriteChar(' ');
                    CursorX = savedX;
                }
                CaptureEraseChars(eraseCount);
                break;
            case 'm': // SGR — Select Graphic Rendition (colors/styles) — ignore
                break;
            case 'n': // DSR — Device Status Report
                InvokeDeviceStatusReport(p0);
                break;
            case 'c': // DA — Device Attributes
                if (p0 == 0)
                {
                    _terminalResponseHandler?.Invoke("\e[?1;0c");
                }
                break;
            case 'q': // DECSCUSR — Set Cursor Style — ignore
            case 'x': // DECREQTPARM — ignore
            case 'g': // TBC — Tab Clear — ignore
            case 'Z': // CBT — Cursor Backward Tabulation — ignore
            case 'I': // CHT — Cursor Horizontal Tabulation — ignore
            case 'b': // REP — Repeat — ignore
            case 'z': // DECERA — ignore
            case '{': // DECSERA — ignore
            case 'p': // various — ignore
            case 't': // Window manipulation
                if (p0 == 18)
                {
                    _terminalResponseHandler?.Invoke($"\e[8;{_dimensions.Rows};{_dimensions.Columns}t");
                }
                break;
        }
    }

    private void DispatchPrivateCsi(char finalByte)
    {
        if (_csiPrefix == '>')
        {
            if (finalByte == 'c')
            {
                // Secondary Device Attributes. Keep the identity conservative.
                _terminalResponseHandler?.Invoke("\e[>0;0;0c");
            }
            return;
        }

        if (_csiPrefix != '?')
        {
            return;
        }

        // Private sequences (prefixed with '?')
        // Most are DECSET/DECRST for terminal modes
        var p0 = GetParam(0, 0);

        switch (finalByte)
        {
            case 'h': // DECSET — Set Mode
            case 'l': // DECRST — Reset Mode
            {
                var enabled = finalByte == 'h';
                if (_csiParams.Count == 0)
                {
                    SetPrivateMode(p0, enabled);
                }
                else
                {
                    foreach (var mode in _csiParams)
                    {
                        SetPrivateMode(mode, enabled);
                    }
                }
                break;
            }
            case 'J': // DECSED — Selective Erase in Display
                CaptureEraseDisplay(p0);
                break;
            case 'K': // DECSEL — Selective Erase in Line
                CaptureEraseLine(p0);
                break;
            case 'm': // SGR with private params — ignore (colors)
                break;
            case 'n': // DSR — Device Status Report
                if (p0 == 6)
                {
                    _terminalResponseHandler?.Invoke(BuildCursorPositionReport(isPrivate: true));
                }
                break;
            // Unknown private CSI — ignore
        }
    }

    #endregion

    #region OSC Processing

    private void ProcessOsc()
    {
        var content = _oscBuffer.ToString();

        // Check for Shell Integration markers: OSC 633 ; <type> [; <data>] ST
        if (content.StartsWith("633;"))
        {
            ProcessShellIntegrationMarker(content);
        }

        // All other OSC sequences are ignored for text extraction.
    }

    /// <summary>
    /// Parse an OSC 633 Shell Integration marker and fire the event.
    /// </summary>
    private void ProcessShellIntegrationMarker(string content)
    {
        // Format: 633 ; <type> [; <data>]
        // content = "633;A" or "633;D;0" or "633;E;command;nonce" etc.
        var parts = content.Split(';');
        if (parts.Length < 2) return;

        HasDetectedShellIntegration = true;
        var markerChar = parts[1];
        switch (markerChar)
        {
            case "A":
            {
                _shellIntegrationMarkerHandler?.Invoke(
                    new ShellIntegrationMarker(
                        ShellIntegrationMarkerType.PromptStart,
                        Line: CursorY));
                break;
            }
            case "B":
            {
                _shellIntegrationMarkerHandler?.Invoke(
                    new ShellIntegrationMarker(
                        ShellIntegrationMarkerType.CommandReady,
                        Line: CursorY));
                break;
            }
            case "C":
            {
                _shellIntegrationMarkerHandler?.Invoke(
                    new ShellIntegrationMarker(
                        ShellIntegrationMarkerType.CommandExecuted,
                        Line: CursorY));
                break;
            }
            case "D":
            {
                // D may have an exit code: 633;D;<exitcode>
                int? exitCode = null;
                if (parts.Length >= 3 && int.TryParse(parts[2], out var code))
                {
                    exitCode = code;
                }
                _shellIntegrationMarkerHandler?.Invoke(
                    new ShellIntegrationMarker(
                        ShellIntegrationMarkerType.CommandFinished,
                        ExitCode: exitCode,
                        Line: CursorY));
                break;
            }
            case "E":
            {
                // E has command text: 633;E;<command>[;<nonce>]
                var cmdLine = parts.Length >= 3 ? DecodeOsc633Value(parts[2]) : null;
                _shellIntegrationMarkerHandler?.Invoke(
                    new ShellIntegrationMarker(
                        ShellIntegrationMarkerType.CommandLine,
                        CommandLine: cmdLine,
                        Line: CursorY));
                break;
            }
        }
    }

    private static string DecodeOsc633Value(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                builder.Append(value[i]);
                continue;
            }

            var next = value[i + 1];
            if (next == '\\')
            {
                builder.Append('\\');
                i++;
                continue;
            }

            if (next == 'x' &&
                i + 3 < value.Length &&
                TryReadHex(value[i + 2], out var high) &&
                TryReadHex(value[i + 3], out var low))
            {
                builder.Append((char)((high << 4) | low));
                i += 3;
                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static bool TryReadHex(char value, out int digit)
    {
        digit = value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1,
        };
        return digit >= 0;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Reset the parser state. Does not reset <see cref="HasDetectedShellIntegration"/>.
    /// </summary>
    public void Reset()
    {
        _state = State.Ground;
        _highSurrogate = '\0';
        ResetCsi();
        _oscBuffer.Clear();
    }

    /// <summary>
    /// Reset the shell integration detection flag.
    /// </summary>
    public void ResetShellIntegrationDetected()
    {
        HasDetectedShellIntegration = false;
    }

    /// <summary>
    /// Update the visible terminal rows used for terminal query responses.
    /// </summary>
    public void Resize(TerminalDimensions dimensions)
    {
        _dimensions = dimensions;
        CursorX = Math.Clamp(CursorX, 0, _dimensions.Columns - 1);
        _savedCursorX = Math.Clamp(_savedCursorX, 0, _dimensions.Columns - 1);
    }

    private void WriteChar(char _)
    {
        if (CursorX >= _dimensions.Columns)
        {
            CursorX = 0;
            CursorY++;
        }

        CursorX++;
    }

    private void CarriageReturn()
    {
        CursorX = 0;
    }

    private void LineFeed()
    {
        if (_scrollBottom >= 0 && CursorY >= ScrollBottom)
        {
            return;
        }

        CursorY++;
    }

    private void Backspace()
    {
        if (CursorX > 0)
        {
            CursorX--;
        }
    }

    private void Tab()
    {
        CursorX = Math.Min((CursorX / 4 + 1) * 4, _dimensions.Columns - 1);
    }

    private void CursorUp(int count = 1)
    {
        CursorY = Math.Max(CursorY - Math.Max(count, 1), _scrollTop);
    }

    private void CursorDown(int count = 1)
    {
        count = Math.Max(count, 1);
        CursorY = _scrollBottom >= 0 ? Math.Min(CursorY + count, ScrollBottom) : CursorY + count;
    }

    private void CursorForward(int count = 1)
    {
        CursorX = Math.Min(CursorX + Math.Max(count, 1), _dimensions.Columns - 1);
    }

    private void CursorBackward(int count = 1)
    {
        CursorX = Math.Max(CursorX - Math.Max(count, 1), 0);
    }

    private void CursorNextLine(int count = 1)
    {
        CursorDown(count);
        CursorX = 0;
    }

    private void CursorPreviousLine(int count = 1)
    {
        CursorUp(count);
        CursorX = 0;
    }

    private void CursorHorizontalAbsolute(int column = 1)
    {
        CursorX = Math.Clamp(column - 1, 0, _dimensions.Columns - 1);
    }

    private void CursorPosition(int row = 1, int column = 1)
    {
        CursorY = Math.Max(row - 1, 0);
        CursorX = Math.Clamp(column - 1, 0, _dimensions.Columns - 1);
    }

    private void SaveCursor()
    {
        _savedCursorX = CursorX;
        _savedCursorY = CursorY;
    }

    private void RestoreCursor()
    {
        CursorX = _savedCursorX;
        CursorY = _savedCursorY;
    }

    private void SetScrollRegion(int top = 1, int bottom = -1)
    {
        _scrollTop = Math.Max(top - 1, 0);
        _scrollBottom = bottom > 0 ? bottom - 1 : -1;
        CursorX = 0;
        CursorY = 0;
    }

    private void BeginCaptureBatch()
    {
        if (_captureUpdateScope is null && _captureBuffer is not null)
        {
            _captureUpdateScope = _captureBuffer.BeginUpdate();
        }
    }

    private void EndCaptureBatch()
    {
        _captureUpdateScope?.Dispose();
        _captureUpdateScope = null;
    }

    private void CaptureWrite(char value)
    {
        _captureBuffer?.Write(value);
    }

    private void CaptureCarriageReturn()
    {
        _captureBuffer?.CarriageReturn();
    }

    private void CaptureLineFeed()
    {
        _captureBuffer?.LineFeed();
    }

    private void CaptureBackspace()
    {
        _captureBuffer?.Backspace();
    }

    private void CaptureTab()
    {
        _captureBuffer?.Tab();
    }

    private void CaptureCursorUp(int count = 1)
    {
        _captureBuffer?.CursorUp(count);
    }

    private void CaptureCursorDown(int count = 1)
    {
        _captureBuffer?.CursorDown(count);
    }

    private void CaptureCursorForward(int count = 1)
    {
        _captureBuffer?.CursorForward(count);
    }

    private void CaptureCursorBackward(int count = 1)
    {
        _captureBuffer?.CursorBackward(count);
    }

    private void CaptureCursorHorizontalAbsoluteFromScreen()
    {
        _captureBuffer?.CursorHorizontalAbsolute(CursorX + 1);
    }

    private void CaptureCursorPositionFromScreen()
    {
        if (_captureBuffer is null)
        {
            return;
        }

        var localRow = Math.Max(1, CursorY - _captureOriginY + 1);
        _captureBuffer.CursorPosition(localRow, CursorX + 1);
    }

    private void CaptureEraseLine(int mode)
    {
        _captureBuffer?.EraseLine(mode);
    }

    private void CaptureEraseDisplay(int mode)
    {
        if (_captureBuffer is null)
        {
            return;
        }

        if (mode is 2 or 3)
        {
            _captureBuffer.Clear();
            return;
        }

        _captureBuffer.EraseDisplay(mode);
    }

    private void CaptureDeleteChars(int count)
    {
        _captureBuffer?.DeleteChars(count);
    }

    private void CaptureInsertChars(int count)
    {
        _captureBuffer?.InsertChars(count);
    }

    private void CaptureEraseChars(int count)
    {
        _captureBuffer?.EraseChars(count);
    }

    private void CaptureClear()
    {
        _captureBuffer?.Clear();
    }

    private void ResetCsi()
    {
        _csiParams.Clear();
        _currentParam = -1;
        _csiPrivateParam = false;
        _csiPrefix = '\0';
        // _csiIntermediate = '\0';
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

    private void InvokeDeviceStatusReport(int request)
    {
        switch (request)
        {
            case 5:
                // "OK" status report.
                _terminalResponseHandler?.Invoke("\e[0n");
                break;
            case 6:
                _terminalResponseHandler?.Invoke(BuildCursorPositionReport(isPrivate: false));
                break;
        }
    }

    private string BuildCursorPositionReport(bool isPrivate)
    {
        var row = Math.Clamp(CursorY + 1, 1, _dimensions.Rows);
        var column = Math.Clamp(CursorX + 1, 1, _dimensions.Columns);
        return isPrivate ? $"\e[?{row};{column}R" : $"\e[{row};{column}R";
    }

    private void SetPrivateMode(int mode, bool enabled)
    {
        switch (mode)
        {
            case 1004:
                IsFocusEventTrackingEnabled = enabled;
                break;
            case 2004:
                IsBracketedPasteModeEnabled = enabled;
                break;
            case 9001:
                IsWin32InputModeEnabled = enabled;
                break;
        }
    }

    #endregion

}
