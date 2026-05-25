using Everywhere.Terminal;

namespace Everywhere.Core.Tests.Terminal;

[TestFixture]
public class TerminalRunTests
{
    [Test]
    public void Constructor_PreservesCommandLineAndOutputLimit()
    {
        var run = new TerminalRun("echo ok", maxOutputLines: 7);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.CommandLine, Is.EqualTo("echo ok"));
            Assert.That(run.ExitCode, Is.Null);
            Assert.That(run.Output.MaxLines, Is.EqualTo(7));
            Assert.That(run.OutputText, Is.EqualTo(string.Empty));
            Assert.That(run.Completion.IsCompleted, Is.False);
        }
    }

    [Test]
    public void OutputWrite_UpdatesOutputText()
    {
        var run = new TerminalRun("echo ok");

        run.Output.Write("alpha\nbeta");

        Assert.That(run.OutputText, Is.EqualTo("alpha\nbeta"));
    }

    [Test]
    public async Task Complete_SetsExitCodeAndReleasesWaiters()
    {
        var run = new TerminalRun("echo ok");
        run.Output.ReplaceText("ok");

        run.Complete(3);
        await run.WaitAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.CommandLine, Is.EqualTo("echo ok"));
            Assert.That(run.ExitCode, Is.EqualTo(3));
            Assert.That(run.OutputText, Is.EqualTo("ok"));
        }
    }

    [Test]
    public void Fail_FaultsWaiters()
    {
        var run = new TerminalRun("echo ok");
        var exception = new InvalidOperationException("boom");

        run.Fail(exception);

        Assert.That(async () => await run.WaitAsync(CancellationToken.None), Throws.InvalidOperationException);
    }
}
