using Everywhere.Automation.TestApp;

namespace Everywhere.Automation.Tests;

public sealed class TestAppProtocolTests
{
    [Test]
    public void Parse_WhenRequiredArgumentsArePresent_ReturnsDeterministicSelection()
    {
        var options = TestAppOptions.Parse("--scenario", "chat", "--seed", "42");

        Assert.Multiple(() =>
        {
            Assert.That(options.Scenario, Is.EqualTo("chat"));
            Assert.That(options.Seed, Is.EqualTo(42));
            Assert.That(options.ResolveScenario().Name, Is.EqualTo("chat"));
        });
    }

    [Test]
    public void Parse_WhenRequiredArgumentIsMissing_RejectsInvocation()
    {
        Assert.That(
            () => TestAppOptions.Parse("--scenario", "chat"),
            Throws.ArgumentException.With.Message.Contains("--seed"));
    }

    [Test]
    public void Serialize_WhenStatusRoundTrips_PreservesRevisionAndRoots()
    {
        var status = new TestAppStatus(
            TestAppStatusKind.Advanced,
            "chat",
            42,
            3,
            3,
            1234,
            [new TestAppRootStatus(0, 100), new TestAppRootStatus(1, 200)],
            [new TestAppAnchorStatus(0, "0/2", "input", "vc-0-2")]);

        var roundTrip = TestAppProtocol.Deserialize<TestAppStatus>(TestAppProtocol.Serialize(status));

        Assert.Multiple(() =>
        {
            Assert.That(roundTrip.Kind, Is.EqualTo(status.Kind));
            Assert.That(roundTrip.Scenario, Is.EqualTo(status.Scenario));
            Assert.That(roundTrip.Seed, Is.EqualTo(status.Seed));
            Assert.That(roundTrip.Step, Is.EqualTo(status.Step));
            Assert.That(roundTrip.Revision, Is.EqualTo(status.Revision));
            Assert.That(roundTrip.ProcessId, Is.EqualTo(status.ProcessId));
            Assert.That(roundTrip.Error, Is.EqualTo(status.Error));
            Assert.That(roundTrip.Roots, Is.EqualTo(status.Roots));
            Assert.That(roundTrip.Anchors, Is.EqualTo(status.Anchors));
        });
    }

    [Test]
    public void Start_WhenCommandLinesAreAvailable_PublishesThemInOrder()
    {
        var input = string.Join(
            Environment.NewLine,
            TestAppProtocol.Serialize(new TestAppCommand(TestAppCommandKind.MoveNext)),
            TestAppProtocol.Serialize(new TestAppCommand(TestAppCommandKind.Stop)));
        var channel = new TestAppControlChannel(new StringReader(input), new StringWriter());
        var commands = new List<TestAppCommandKind>();
        using var completed = new ManualResetEventSlim();
        channel.CommandReceived += command =>
        {
            lock (commands)
            {
                commands.Add(command.Kind);
                if (commands.Count == 2)
                {
                    completed.Set();
                }
            }
        };

        channel.Start();

        Assert.That(completed.Wait(TimeSpan.FromSeconds(2)), Is.True);
        lock (commands)
        {
            Assert.That(commands, Is.EqualTo(new[] { TestAppCommandKind.MoveNext, TestAppCommandKind.Stop }));
        }
    }
}
