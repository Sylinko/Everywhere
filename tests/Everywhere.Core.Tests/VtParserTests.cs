using Everywhere.Chat.Plugins.BuiltIn.Terminal;

namespace Everywhere.Core.Tests;

/// <summary>
/// Unit tests for <see cref="VtSequenceParser"/> and <see cref="VirtualTerminalBuffer"/>.
/// </summary>
[TestFixture]
public class VtParserTests
{
    #region VirtualTerminalBuffer — Basic Operations

    [Test]
    public void Buffer_WriteChar_AdvancesCursor()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteChar('A');
        Assert.That(buffer.CursorX, Is.EqualTo(1));
        Assert.That(buffer.CursorY, Is.EqualTo(0));
    }

    [Test]
    public void Buffer_WriteString_PlacesText()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("hello");
        Assert.That(buffer.GetText(), Is.EqualTo("hello"));
    }

    [Test]
    public void Buffer_CarriageReturn_ResetsCursorX()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("hello");
        buffer.CarriageReturn();
        Assert.That(buffer.CursorX, Is.EqualTo(0));
        Assert.That(buffer.CursorY, Is.EqualTo(0));
    }

    [Test]
    public void Buffer_LineFeed_MovesCursorDown()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("line1");
        buffer.CarriageReturn();
        buffer.LineFeed();
        Assert.That(buffer.CursorY, Is.EqualTo(1));
    }

    [Test]
    public void Buffer_Backspace_MovesCursorLeft()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("hello"); // cursor at X=5
        buffer.Backspace();          // cursor at X=4
        Assert.That(buffer.CursorX, Is.EqualTo(4));
    }

    [Test]
    public void Buffer_Tab_MovesToNextTabStop()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.Tab();
        Assert.That(buffer.CursorX, Is.EqualTo(8));
    }

    [Test]
    public void Buffer_LineWrap_OnFullColumn()
    {
        var buffer = new VirtualTerminalBuffer(5);
        buffer.WriteString("abcdef"); // 6 chars in 5-col buffer
        Assert.That(buffer.CursorX, Is.EqualTo(1)); // wrapped to next line
        Assert.That(buffer.CursorY, Is.EqualTo(1));
    }

    #endregion

    #region VirtualTerminalBuffer — Text Extraction

    [Test]
    public void GetText_MultipleLines()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("line1");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line2");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line3");

        var text = buffer.GetText();
        Assert.That(text, Is.EqualTo("line1\nline2\nline3"));
    }

    [Test]
    public void GetText_EmptyBuffer_ReturnsEmpty()
    {
        var buffer = new VirtualTerminalBuffer(80);
        Assert.That(buffer.GetText(), Is.EqualTo(string.Empty));
    }

    [Test]
    public void GetTextBetween_SpecificRange()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("line0");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line1");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line2");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line3");

        var text = buffer.GetTextBetween(1, 2);
        Assert.That(text, Is.EqualTo("line1\nline2"));
    }

    [Test]
    public void GetTextBetween_OutOfRange_Clamps()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("line0");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line1");

        var text = buffer.GetTextBetween(0, 100);
        Assert.That(text, Is.EqualTo("line0\nline1"));
    }

    [Test]
    public void GetTextBetween_StartAfterEnd_ReturnsEmpty()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("hello");
        var text = buffer.GetTextBetween(5, 3);
        Assert.That(text, Is.EqualTo(string.Empty));
    }

    [Test]
    public void GetLastLine_ReturnsLastNonEmptyLine()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("first");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("second");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("third");

        Assert.That(buffer.GetLastLine(), Is.EqualTo("third"));
    }

    [Test]
    public void GetCursorLine_ReturnsCurrentLine()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("line0");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line1");

        Assert.That(buffer.GetCursorLine(), Is.EqualTo("line1"));
    }

    #endregion

    #region VirtualTerminalBuffer — Cursor Movement

    [Test]
    public void CursorUp_DecreasesY()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("line0");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line1");
        buffer.CursorUp();
        Assert.That(buffer.CursorY, Is.EqualTo(0));
    }

    [Test]
    public void CursorDown_IncreasesY()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("line0");
        buffer.CursorDown();
        Assert.That(buffer.CursorY, Is.EqualTo(1));
    }

    [Test]
    public void CursorForward_IncreasesX()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("hello");
        buffer.CursorBackward(5);
        buffer.CursorForward(3);
        Assert.That(buffer.CursorX, Is.EqualTo(3));
    }

    [Test]
    public void CursorPosition_MovesToAbsolute()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.CursorPosition(3, 5); // row=3, col=5 (1-based)
        Assert.That(buffer.CursorY, Is.EqualTo(2)); // 0-based
        Assert.That(buffer.CursorX, Is.EqualTo(4)); // 0-based
    }

    [Test]
    public void CursorHorizontalAbsolute_MovesToColumn()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("hello");
        buffer.CursorHorizontalAbsolute(1); // Move to column 1 (0-based index 0)
        Assert.That(buffer.CursorX, Is.EqualTo(0));
    }

    #endregion

    #region VtSequenceParser — Basic Text

    [Test]
    public void Feed_PrintableText_WrittenToBuffer()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("hello world");
        Assert.That(buffer.GetText(), Is.EqualTo("hello world"));
    }

    [Test]
    public void Feed_NewlineAdvancesLine()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        // Real PTY output uses \r\n (CR+LF). LF alone only moves down without resetting column.
        parser.Feed("line1\r\nline2");
        Assert.That(buffer.GetText(), Is.EqualTo("line1\nline2"));
    }

    [Test]
    public void Feed_CarriageReturn_ResetsColumn()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("hello\rworld");
        // CR overwrites from column 0
        Assert.That(buffer.GetText(), Is.EqualTo("world"));
    }

    [Test]
    public void Feed_EmptyString_NoChange()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("");
        Assert.That(buffer.GetText(), Is.EqualTo(string.Empty));
    }

    #endregion

    #region VtSequenceParser — ANSI Sequences

    [Test]
    public void Feed_AnsiColor_DoesNotAffectText()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        // \e[31m = set red foreground, \e[0m = reset
        parser.Feed("\e[31mhello\e[0m");
        Assert.That(buffer.GetText(), Is.EqualTo("hello"));
    }

    [Test]
    public void Feed_CursorMovement_CSI()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        // Write "a", move cursor right 5, write "b"
        parser.Feed("a\e[5Cb");
        Assert.That(buffer.CursorX, Is.EqualTo(7)); // a(1) + 5 + b(1)
    }

    [Test]
    public void Feed_EraseLine_ClearsLine()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("hello\r\e[2K"); // Write hello, CR, erase entire line
        var text = buffer.GetCursorLine();
        // After erase, the line should be empty (all spaces or null chars)
        Assert.That(text.Trim(), Is.EqualTo(string.Empty));
    }

    [Test]
    public void Feed_ClearScreen_ResetsBuffer()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("line1\nline2\e[2J"); // Write two lines, then clear screen
        // After clear screen, text should be empty
        Assert.That(buffer.GetText().Trim(), Is.EqualTo(string.Empty));
    }

    #endregion

    #region VtSequenceParser — Shell Integration Markers

    [Test]
    public void Feed_ShellIntegrationMarker_DetectsShellIntegration()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        // OSC 633;A = PromptStart
        parser.Feed("\e]633;A\x07");
        Assert.That(parser.HasDetectedShellIntegration, Is.True);
    }

    [Test]
    public void Feed_ShellIntegrationMarker_FiresHandler()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var markers = new List<ShellIntegrationMarker>();
        var parser = new VtSequenceParser(buffer, (in ShellIntegrationMarker m) => markers.Add(m));

        // A = PromptStart
        parser.Feed("\e]633;A\x07");
        // B = CommandReady
        parser.Feed("\e]633;B\x07");
        // C = CommandExecuted
        parser.Feed("\e]633;C\x07");
        // D with exit code = CommandFinished
        parser.Feed("\e]633;D;0\x07");

        Assert.That(markers, Has.Count.EqualTo(4));
        Assert.That(markers[0].Type, Is.EqualTo(ShellIntegrationMarkerType.PromptStart));
        Assert.That(markers[1].Type, Is.EqualTo(ShellIntegrationMarkerType.CommandReady));
        Assert.That(markers[2].Type, Is.EqualTo(ShellIntegrationMarkerType.CommandExecuted));
        Assert.That(markers[3].Type, Is.EqualTo(ShellIntegrationMarkerType.CommandFinished));
        Assert.That(markers[3].ExitCode, Is.EqualTo(0));
    }

    [Test]
    public void Feed_ShellIntegrationMarker_D_WithExitCode()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var markers = new List<ShellIntegrationMarker>();
        var parser = new VtSequenceParser(buffer, (in ShellIntegrationMarker m) => markers.Add(m));

        parser.Feed("\e]633;D;42\x07");

        Assert.That(markers, Has.Count.EqualTo(1));
        Assert.That(markers[0].Type, Is.EqualTo(ShellIntegrationMarkerType.CommandFinished));
        Assert.That(markers[0].ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void Feed_ShellIntegrationMarker_E_WithCommandLine()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var markers = new List<ShellIntegrationMarker>();
        var parser = new VtSequenceParser(buffer, (in ShellIntegrationMarker m) => markers.Add(m));

        parser.Feed("\e]633;E;echo hello;nonce123\x07");

        Assert.That(markers, Has.Count.EqualTo(1));
        Assert.That(markers[0].Type, Is.EqualTo(ShellIntegrationMarkerType.CommandLine));
        Assert.That(markers[0].CommandLine, Is.EqualTo("echo hello"));
    }

    [Test]
    public void Feed_NoShellIntegration_DetectedFalse()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("just normal text\nno markers here");
        Assert.That(parser.HasDetectedShellIntegration, Is.False);
    }

    #endregion

    #region VtSequenceParser — ST Terminator

    [Test]
    public void Feed_ShellIntegration_ST_Terminator()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var markers = new List<ShellIntegrationMarker>();
        var parser = new VtSequenceParser(buffer, (in ShellIntegrationMarker m) => markers.Add(m));

        // OSC terminated with ESC\ (String Terminator) instead of BEL
        parser.Feed("\e]633;A\e\\");

        Assert.That(parser.HasDetectedShellIntegration, Is.True);
        Assert.That(markers, Has.Count.EqualTo(1));
        Assert.That(markers[0].Type, Is.EqualTo(ShellIntegrationMarkerType.PromptStart));
    }

    #endregion

    #region VtSequenceParser — Unicode

    [Test]
    public void Feed_UnicodeText()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("你好世界");
        Assert.That(buffer.GetText(), Is.EqualTo("你好世界"));
    }

    [Test]
    public void Feed_Emoji_SurrogatePairs()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("hello 🌍 world");
        // Emoji may be written as two chars in the buffer (surrogate pair)
        var text = buffer.GetText();
        Assert.That(text, Does.Contain("hello"));
        Assert.That(text, Does.Contain("world"));
    }

    #endregion

    #region VtSequenceParser — Reset

    [Test]
    public void Reset_ClearsParserState()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("hello");
        // Parser.Reset() resets the parser state machine (CSI params, OSC buffer, etc.)
        // but does NOT reset the buffer or cursor position.
        parser.Reset();
        // Cursor is still at X=5 after "hello", so "world" appends
        parser.Feed("world");
        Assert.That(buffer.GetText(), Is.EqualTo("helloworld"));
    }

    [Test]
    public void ResetShellIntegrationDetected_ClearsFlag()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("\e]633;A\x07");
        Assert.That(parser.HasDetectedShellIntegration, Is.True);
        parser.ResetShellIntegrationDetected();
        Assert.That(parser.HasDetectedShellIntegration, Is.False);
    }

    #endregion
}
