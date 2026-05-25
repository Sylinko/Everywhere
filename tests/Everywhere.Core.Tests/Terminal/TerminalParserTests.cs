using Everywhere.Terminal;

namespace Everywhere.Core.Tests.Terminal;

[TestFixture]
public class TerminalParserTests
{
    [Test]
    public void Feed_Capture_TracksGroundTextButSkipsControlSequences()
    {
        var parser = new TerminalParser();
        var output = new TerminalLineBuffer();

        parser.BeginCapture(output);
        parser.Feed("A\e[31mB\e[0m\e]633;A\a\r\nC");
        parser.EndCapture();

        Assert.That(output.GetText(), Is.EqualTo("AB\nC"));
    }

    [Test]
    public void Feed_Capture_WritesOnlyBetweenBeginAndEnd()
    {
        var parser = new TerminalParser();
        var output = new TerminalLineBuffer();

        parser.Feed("before");
        parser.BeginCapture(output);
        parser.Feed("one\r\ntwo");
        parser.EndCapture();
        parser.Feed("after");

        Assert.That(output.GetText(), Is.EqualTo("one\ntwo"));
    }

    [Test]
    public void Feed_Capture_EraseLineKeepsFinalVisibleState()
    {
        var parser = new TerminalParser();
        var output = new TerminalLineBuffer();

        parser.BeginCapture(output);
        parser.Feed("progress 10%\r\e[2Kdone");
        parser.EndCapture();

        Assert.That(output.GetText(), Is.EqualTo("done"));
    }

    [Test]
    public void Feed_Capture_ClearDisplayClearsCurrentRun()
    {
        var parser = new TerminalParser();
        var output = new TerminalLineBuffer();

        parser.BeginCapture(output);
        parser.Feed("one\r\ntwo\e[2Jafter");
        parser.EndCapture();

        Assert.That(output.GetText(), Is.EqualTo("after"));
    }

    [Test]
    public void Feed_Capture_AbsoluteCursorPositionMapsToCaptureRelativeRow()
    {
        var parser = new TerminalParser();
        var output = new TerminalLineBuffer();

        parser.Feed("prompt\r\n");
        parser.BeginCapture(output);
        parser.Feed("one\r\ntwo\e[1;1Htop");
        parser.EndCapture();

        Assert.That(output.GetText(), Is.EqualTo("top\ntwo"));
    }

    [Test]
    public void Feed_Capture_PreservesFinalVisibleState_DuringPsReadlineRedraw()
    {
        var parser = new TerminalParser();
        var output = new TerminalLineBuffer();

        parser.BeginCapture(output);
        parser.Feed("LINE_1\r\nLINE_2\r\nLINE_3");
        parser.Feed("\e[2A");
        parser.Feed("\r\e[2KLINE_3\r\n");
        parser.Feed("\r\e[2KLINE_2\r\n");
        parser.Feed("\r\e[2KLINE_1\r\n");
        parser.EndCapture();

        Assert.That(output.GetText(), Is.EqualTo("LINE_3\nLINE_2\nLINE_1"));
    }

    [Test]
    public void Feed_DetectsBracketedPasteMode()
    {
        var parser = new TerminalParser();

        parser.Feed("\e[?2004h");

        Assert.That(parser.IsBracketedPasteModeEnabled, Is.True);
    }

    [Test]
    public void Feed_DecodesEscapedShellIntegrationCommandLine()
    {
        var commandLines = new List<string?>();
        var parser = new TerminalParser(
            shellIntegrationMarkerHandler: (in marker) =>
            {
                if (marker.Type == ShellIntegrationMarkerType.CommandLine)
                {
                    commandLines.Add(marker.CommandLine);
                }
            });

        parser.Feed("\e]633;E;pwd\\x0als -F\\x3becho \\\\ok\a");

        Assert.That(commandLines, Is.EqualTo(new[] { "pwd\nls -F;echo \\ok" }));
    }

    [Test]
    public void Feed_EmitsTerminalQueryResponses()
    {
        var responses = new List<string>();
        var parser = new TerminalParser(
            terminalResponseHandler: responses.Add,
            dimensions: new TerminalDimensions(77, 33));

        parser.Feed("\e[18t\e[6n");

        Assert.That(responses, Does.Contain("\e[8;33;77t"));
        Assert.That(responses, Does.Contain("\e[1;1R"));
    }
}
