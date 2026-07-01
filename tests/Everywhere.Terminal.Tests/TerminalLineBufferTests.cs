namespace Everywhere.Terminal.Tests;

[TestFixture]
public class TerminalLineBufferTests
{
    [Test]
    public void Write_TextAndNewlines_CreatesStableLines()
    {
        var buffer = new TerminalLineBuffer();

        buffer.Write("alpha\nbeta");

        Assert.That(buffer.Count, Is.EqualTo(2));
        Assert.That(buffer[0].Text, Is.EqualTo("alpha"));
        Assert.That(buffer[1].Text, Is.EqualTo("beta"));
        Assert.That(buffer[0].Id, Is.Not.EqualTo(buffer[1].Id));
        Assert.That(buffer.GetText(), Is.EqualTo("alpha\nbeta"));
    }

    [Test]
    public void Write_CarriageReturn_OverwritesCurrentLine()
    {
        var buffer = new TerminalLineBuffer();

        buffer.Write("progress 10%");
        buffer.Write("\rprogress 20%");

        Assert.That(buffer.Count, Is.EqualTo(1));
        Assert.That(buffer[0].Text, Is.EqualTo("progress 20%"));
    }

    [Test]
    public void Write_TrailingSpaces_AreTrimmedFromStoredLines()
    {
        var buffer = new TerminalLineBuffer();

        buffer.Write("alpha   \n");
        buffer.CursorForward(20);
        buffer.Write(" ");

        Assert.That(buffer.GetText(), Is.EqualTo("alpha"));
        Assert.That(buffer[0].Text, Is.EqualTo("alpha"));
        Assert.That(buffer[1].Text, Is.EqualTo(string.Empty));
    }

    [Test]
    public void CopyLines_OmitsTrailingEmptyLiveLine()
    {
        var buffer = new TerminalLineBuffer();

        buffer.Write("alpha\n");

        var lines = buffer.CopyLines(null, out _);

        Assert.That(buffer.Count, Is.EqualTo(2));
        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0].Text, Is.EqualTo("alpha"));
    }

    [Test]
    public void Write_ContiguousText_EmitsSingleChangedEvent()
    {
        var buffer = new TerminalLineBuffer();
        var changed = 0;
        buffer.Changed += (_, _) => changed++;

        buffer.Write("abcdef");

        Assert.That(changed, Is.EqualTo(1));
        Assert.That(buffer[0].Text, Is.EqualTo("abcdef"));
    }

    [Test]
    public void BeginUpdate_EmitsSingleChangedEventForRepeatedReplace()
    {
        var buffer = new TerminalLineBuffer();
        buffer.Write("alpha");

        var changed = 0;
        buffer.Changed += (_, _) => changed++;

        using (buffer.BeginUpdate())
        {
            buffer.Write("\rbeta");
            buffer.Write("\rgamma");
        }

        Assert.That(changed, Is.EqualTo(1));
        Assert.That(buffer.GetText(), Is.EqualTo("gamma"));
    }

    [Test]
    public void Write_Backspace_ReplacesPreviousCharacter()
    {
        var buffer = new TerminalLineBuffer();

        buffer.Write("abc\bX");

        Assert.That(buffer.GetText(), Is.EqualTo("abX"));
    }

    [Test]
    public void EraseLine_ModeZero_RemovesTextAfterCursor()
    {
        var buffer = new TerminalLineBuffer();

        buffer.Write("abcdef");
        buffer.Write("\rabc");
        buffer.EraseLine();

        Assert.That(buffer.GetText(), Is.EqualTo("abc"));
    }

    [Test]
    public void EraseLine_ModeOne_RemovesTextBeforeAndAtCursor()
    {
        var buffer = new TerminalLineBuffer();

        buffer.Write("abcdef");
        buffer.CursorHorizontalAbsolute(4);
        buffer.EraseLine(1);

        Assert.That(buffer[0].Text, Is.EqualTo("    ef"));
    }

    [Test]
    public void EraseLine_ModeTwo_ClearsCurrentLine()
    {
        var buffer = new TerminalLineBuffer();

        buffer.Write("abcdef");
        buffer.EraseLine(2);

        Assert.That(buffer.GetText(), Is.EqualTo(string.Empty));
        Assert.That(buffer.Count, Is.EqualTo(1));
    }

    [Test]
    public void EraseDisplay_ModeZero_RemovesCurrentLineTailAndFollowingLines()
    {
        var buffer = new TerminalLineBuffer();

        buffer.Write("one\ntwo\nthree");
        buffer.CursorPosition(2, 2);
        buffer.EraseDisplay();

        Assert.That(buffer.GetText(), Is.EqualTo("one\nt"));
    }

    [Test]
    public void EraseDisplay_ModeTwo_ClearsBuffer()
    {
        var buffer = new TerminalLineBuffer();

        buffer.Write("one\ntwo");
        buffer.EraseDisplay(2);

        Assert.That(buffer.Count, Is.Zero);
        Assert.That(buffer.GetText(), Is.EqualTo(string.Empty));
    }

    [Test]
    public void CursorUp_AllowsOverwritingPreviousLine()
    {
        var buffer = new TerminalLineBuffer();

        buffer.Write("one\ntwo");
        buffer.CursorUp();
        buffer.CarriageReturn();
        buffer.Write("ONE");

        Assert.That(buffer.GetText(), Is.EqualTo("ONE\ntwo"));
    }

    [Test]
    public void CursorPosition_PadsIntermediateLines()
    {
        var buffer = new TerminalLineBuffer();

        buffer.CursorPosition(3, 4);
        buffer.Write("x");

        Assert.That(buffer.Count, Is.EqualTo(3));
        Assert.That(buffer[2].Text, Is.EqualTo("   x"));
    }

    [Test]
    public void DeleteChars_RemovesCharactersFromCursor()
    {
        var buffer = new TerminalLineBuffer();

        buffer.Write("abcdef");
        buffer.CursorHorizontalAbsolute(3);
        buffer.DeleteChars(2);

        Assert.That(buffer.GetText(), Is.EqualTo("abef"));
    }

    [Test]
    public void EraseChars_ReplacesCharactersFromCursorWithSpaces()
    {
        var buffer = new TerminalLineBuffer();

        buffer.Write("abcdef");
        buffer.CursorHorizontalAbsolute(3);
        buffer.EraseChars(2);

        Assert.That(buffer.GetText(), Is.EqualTo("ab  ef"));
    }

    [Test]
    public void InsertChars_InsertsSpacesAtCursor()
    {
        var buffer = new TerminalLineBuffer();

        buffer.Write("abcdef");
        buffer.CursorHorizontalAbsolute(3);
        buffer.InsertChars(2);

        Assert.That(buffer.GetText(), Is.EqualTo("ab  cdef"));
    }

    [Test]
    public void MaxLines_TrimsOldestLines()
    {
        var buffer = new TerminalLineBuffer(maxLines: 2);

        buffer.Write("one\ntwo\nthree");

        Assert.That(buffer.Count, Is.EqualTo(2));
        Assert.That(buffer[0].Text, Is.EqualTo("two"));
        Assert.That(buffer[1].Text, Is.EqualTo("three"));
    }

    [Test]
    public void MaxLines_TrimmingPreservesRemainingLineIds()
    {
        var buffer = new TerminalLineBuffer(maxLines: 3);
        buffer.Write("one\ntwo\nthree");
        var twoId = buffer[1].Id;
        var threeId = buffer[2].Id;

        buffer.Write("\nfour");

        Assert.That(buffer.Count, Is.EqualTo(3));
        Assert.That(buffer[0].Text, Is.EqualTo("two"));
        Assert.That(buffer[1].Text, Is.EqualTo("three"));
        Assert.That(buffer[0].Id, Is.EqualTo(twoId));
        Assert.That(buffer[1].Id, Is.EqualTo(threeId));
    }

    [Test]
    public void BeginUpdate_EmitsSingleChangedEvent()
    {
        var buffer = new TerminalLineBuffer();
        var changed = 0;
        buffer.Changed += (_, _) => changed++;

        using (buffer.BeginUpdate())
        {
            buffer.Write("alpha\nbeta");
            buffer.Write("\rgamma");
        }

        Assert.That(changed, Is.EqualTo(1));
    }

    [Test]
    public void NoOpReplace_DoesNotRaiseChangedEvent()
    {
        var buffer = new TerminalLineBuffer();
        buffer.Write("same");

        var changed = 0;
        buffer.Changed += (_, _) => changed++;

        using (buffer.BeginUpdate())
        {
            buffer.CarriageReturn();
            buffer.Write("same");
        }

        Assert.That(changed, Is.Zero);
        Assert.That(buffer.GetText(), Is.EqualTo("same"));
    }

    [Test]
    public void ReplaceText_ResetsAndWritesNewContent()
    {
        var buffer = new TerminalLineBuffer();
        buffer.Write("old");

        buffer.ReplaceText("new\nvalue");

        Assert.That(buffer.GetText(), Is.EqualTo("new\nvalue"));
    }
}
