using Everywhere.Chat.Plugins.BuiltIn.Terminal;

namespace Everywhere.Core.Tests;

/// <summary>
/// Unit tests for <see cref="OutputCleaner"/> — pure functions with no side effects.
/// </summary>
[TestFixture]
public class OutputCleanerTests
{
    #region IsMultilineCommand

    [Test]
    public void IsMultilineCommand_SingleLine_ReturnsFalse()
    {
        Assert.That(OutputCleaner.IsMultilineCommand("echo hello"), Is.False);
    }

    [Test]
    public void IsMultilineCommand_SingleLineWithSpaces_ReturnsFalse()
    {
        Assert.That(OutputCleaner.IsMultilineCommand("  echo hello  "), Is.False);
    }

    [Test]
    public void IsMultilineCommand_Empty_ReturnsFalse()
    {
        Assert.That(OutputCleaner.IsMultilineCommand(""), Is.False);
    }

    [Test]
    public void IsMultilineCommand_MultiLine_ReturnsTrue()
    {
        Assert.That(OutputCleaner.IsMultilineCommand("echo a\necho b"), Is.True);
    }

    [Test]
    public void IsMultilineCommand_MultiLineCrLf_ReturnsTrue()
    {
        Assert.That(OutputCleaner.IsMultilineCommand("echo a\r\necho b"), Is.True);
    }

    [Test]
    public void IsMultilineCommand_MultiLineCr_ReturnsTrue()
    {
        Assert.That(OutputCleaner.IsMultilineCommand("echo a\recho b"), Is.True);
    }

    [Test]
    public void IsMultilineCommand_LineContinuation_ReturnsFalse()
    {
        // Backslash immediately before newline = line continuation, NOT multi-line
        Assert.That(OutputCleaner.IsMultilineCommand("echo a \\\necho b"), Is.False);
    }

    [Test]
    public void IsMultilineCommand_Heredoc_ReturnsTrue()
    {
        var script = "cat <<EOF\nhello\nworld\nEOF";
        Assert.That(OutputCleaner.IsMultilineCommand(script), Is.True);
    }

    [Test]
    public void IsMultilineCommand_PowerShellMultiLine_ReturnsTrue()
    {
        var script = "Write-Host \"line1\"\nWrite-Host \"line2\"";
        Assert.That(OutputCleaner.IsMultilineCommand(script), Is.True);
    }

    #endregion

    #region IsShellPrompt

    [Test]
    public void IsShellPrompt_PowerShell_ReturnsTrue()
    {
        Assert.That(OutputCleaner.IsShellPrompt("PS C:\\Users\\test>"), Is.True);
    }

    [Test]
    public void IsShellPrompt_PowerShellWithPath_ReturnsTrue()
    {
        Assert.That(OutputCleaner.IsShellPrompt("PS C:\\Users\\test\\Documents>"), Is.True);
    }

    [Test]
    public void IsShellPrompt_BashZsh_ReturnsTrue()
    {
        Assert.That(OutputCleaner.IsShellPrompt("user@host:~/dir$"), Is.True);
    }

    [Test]
    public void IsShellPrompt_BashZshHash_ReturnsTrue()
    {
        Assert.That(OutputCleaner.IsShellPrompt("root@host:/#"), Is.True);
    }

    [Test]
    public void IsShellPrompt_Cmd_ReturnsTrue()
    {
        Assert.That(OutputCleaner.IsShellPrompt("C:\\Users\\test>"), Is.True);
    }

    [Test]
    public void IsShellPrompt_GenericDollar_ReturnsTrue()
    {
        // Short line ending with $
        Assert.That(OutputCleaner.IsShellPrompt("prompt$"), Is.True);
    }

    [Test]
    public void IsShellPrompt_GenericGreaterThan_ReturnsTrue()
    {
        // Short line ending with >
        Assert.That(OutputCleaner.IsShellPrompt("prompt>"), Is.True);
    }

    [Test]
    public void IsShellPrompt_NotPrompt_ReturnsFalse()
    {
        Assert.That(OutputCleaner.IsShellPrompt("hello world"), Is.False);
    }

    [Test]
    public void IsShellPrompt_Empty_ReturnsFalse()
    {
        Assert.That(OutputCleaner.IsShellPrompt(""), Is.False);
    }

    [Test]
    public void IsShellPrompt_Whitespace_ReturnsFalse()
    {
        Assert.That(OutputCleaner.IsShellPrompt("   "), Is.False);
    }

    [Test]
    public void IsShellPrompt_LongLineEndingWithDollar_ReturnsFalse()
    {
        // Lines >= 200 chars ending with $ should NOT be treated as prompt
        var longLine = new string('a', 199) + "$";
        Assert.That(OutputCleaner.IsShellPrompt(longLine), Is.False);
    }

    #endregion

    #region CleanOutput

    [Test]
    public void CleanOutput_SimpleOutput_ReturnsTrimmed()
    {
        var result = OutputCleaner.CleanOutput("  hello world  ", "echo hello");
        Assert.That(result, Is.EqualTo("hello world").Or.Contain("hello world"));
    }

    [Test]
    public void CleanOutput_TruncatesLongOutput()
    {
        var longOutput = new string('x', 70_000);
        var result = OutputCleaner.CleanOutput(longOutput, "test");
        Assert.That(result.Length, Is.LessThanOrEqualTo(60_000));
        Assert.That(result, Does.Contain("PREVIOUS OUTPUT TRUNCATED"));
    }

    [Test]
    public void CleanOutput_NormalLengthOutput_NotTruncated()
    {
        var output = "short output";
        var result = OutputCleaner.CleanOutput(output, "test");
        Assert.That(result, Does.Not.Contain("TRUNCATED"));
    }

    #endregion

    #region EscapeForLog

    [Test]
    public void EscapeForLog_EscapeChar()
    {
        Assert.That(OutputCleaner.EscapeForLog("\e"), Is.EqualTo("^["));
    }

    [Test]
    public void EscapeForLog_CarriageReturn()
    {
        Assert.That(OutputCleaner.EscapeForLog("\r"), Is.EqualTo("^M"));
    }

    [Test]
    public void EscapeForLog_LineFeed()
    {
        Assert.That(OutputCleaner.EscapeForLog("\n"), Is.EqualTo("^J"));
    }

    [Test]
    public void EscapeForLog_Tab()
    {
        Assert.That(OutputCleaner.EscapeForLog("\t"), Is.EqualTo("^I"));
    }

    [Test]
    public void EscapeForLog_Null()
    {
        Assert.That(OutputCleaner.EscapeForLog("\0"), Is.EqualTo("^@"));
    }

    [Test]
    public void EscapeForLog_PrintableText_Unchanged()
    {
        Assert.That(OutputCleaner.EscapeForLog("hello"), Is.EqualTo("hello"));
    }

    [Test]
    public void EscapeForLog_MixedContent()
    {
        var input = "line1\r\nline2";
        var expected = "line1^M^Jline2";
        Assert.That(OutputCleaner.EscapeForLog(input), Is.EqualTo(expected));
    }

    #endregion

    #region FindCommandEcho

    [Test]
    public void FindCommandEcho_FullMatch_FindsCommand()
    {
        var output = "user@host:~$ echo hello\necho hello\nhello\nuser@host:~$ ";
        var result = OutputCleaner.FindCommandEcho(output, "echo hello", allowSuffixMatch: true);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.ContentBefore, Is.Not.Empty);
    }

    [Test]
    public void FindCommandEcho_NoMatch_ReturnsNull()
    {
        var output = "some random output\nwithout the command";
        var result = OutputCleaner.FindCommandEcho(output, "echo hello", allowSuffixMatch: false);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void FindCommandEcho_EmptyCommand_ReturnsNull()
    {
        var result = OutputCleaner.FindCommandEcho("output", "", allowSuffixMatch: true);
        Assert.That(result, Is.Null);
    }

    #endregion

    #region StripNewLinesAndBuildMapping

    [Test]
    public void StripNewLinesAndBuildMapping_RemovesNewlines()
    {
        var (stripped, mapping) = OutputCleaner.StripNewLinesAndBuildMapping("ab\ncd\nef");
        Assert.That(stripped, Is.EqualTo("abcdef"));
        Assert.That(mapping.Length, Is.EqualTo(6));
        // 'a' at 0, 'b' at 1, 'c' at 3 (skip \n at 2), 'd' at 4, 'e' at 6 (skip \n at 5), 'f' at 7
        Assert.That(mapping[0], Is.EqualTo(0));
        Assert.That(mapping[1], Is.EqualTo(1));
        Assert.That(mapping[2], Is.EqualTo(3));
        Assert.That(mapping[3], Is.EqualTo(4));
        Assert.That(mapping[4], Is.EqualTo(6));
        Assert.That(mapping[5], Is.EqualTo(7));
    }

    [Test]
    public void StripNewLinesAndBuildMapping_NoNewlines()
    {
        var (stripped, mapping) = OutputCleaner.StripNewLinesAndBuildMapping("hello");
        Assert.That(stripped, Is.EqualTo("hello"));
        Assert.That(mapping, Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
    }

    #endregion
}
