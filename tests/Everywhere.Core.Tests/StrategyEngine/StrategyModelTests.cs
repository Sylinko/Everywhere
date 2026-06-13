using Everywhere.Chat;
using Everywhere.I18N;
using Everywhere.StrategyEngine;
using MessagePack;

namespace Everywhere.Core.Tests.StrategyEngine;

public class StrategyModelTests
{
    [Test]
    public void StrategyOptions_DefaultsMatchV1Spec()
    {
        var options = StrategyOptions.Default;

        Assert.Multiple(() =>
        {
            Assert.That(options.MatchingTimeout, Is.EqualTo(TimeSpan.FromMilliseconds(300)));
            Assert.That(options.ConditionTimeout, Is.EqualTo(TimeSpan.FromMilliseconds(80)));
            Assert.That(options.RegexTimeout, Is.EqualTo(TimeSpan.FromMilliseconds(50)));
            Assert.That(options.VisualQueryTimeout, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
            Assert.That(options.ExtraTimeout, Is.EqualTo(TimeSpan.FromMilliseconds(200)));
            Assert.That(options.PreprocessorTimeout, Is.EqualTo(TimeSpan.FromSeconds(2)));
        });
    }

    [Test]
    public void Strategy_RuntimeDefaultsAreSafeForExistingBuiltinConstruction()
    {
        var strategy = CreateStrategy("help");

        Assert.Multiple(() =>
        {
            Assert.That(strategy.Source, Is.EqualTo(StrategySource.Unknown));
            Assert.That(strategy.Includes, Is.Empty);
            Assert.That(strategy.Preprocessors, Is.Empty);
            Assert.That(strategy.Options, Is.EqualTo(StrategyOptions.Default));
            Assert.That(strategy.Metadata, Is.Empty);
        });
    }

    [Test]
    public void StrategyRegistry_AssignsProviderNamespaceAndSource()
    {
        var registry = new StrategyRegistry([new TestStrategyProvider("builtin", CreateStrategy("help"))]);

        var strategy = registry.GetRegisteredStrategies().Single();

        Assert.Multiple(() =>
        {
            Assert.That(strategy.Id, Is.EqualTo("builtin.help"));
            Assert.That(strategy.Source.ProviderId, Is.EqualTo("builtin"));
            Assert.That(strategy.Source.IsBuiltin, Is.True);
            Assert.That(strategy.Source.Location, Is.EqualTo(new Uri("strategy://builtin/help")));
        });
    }

    [Test]
    public void StrategyEngine_ReturnsProviderSuppliedStrategies()
    {
        var strategy = CreateStrategy("provider-supplied") with { Priority = 1 };
        var registry = new StrategyRegistry([new TestStrategyProvider("builtin", strategy)]);
        var engine = new global::Everywhere.StrategyEngine.StrategyEngine(
            registry,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<global::Everywhere.StrategyEngine.StrategyEngine>.Instance);

        var strategies = engine.GetStrategies(StrategyContext.FromAttachments([]));

        Assert.That(strategies.Select(s => s.Id), Is.EqualTo(["builtin.provider-supplied"]));
    }

    [Test]
    public void UserStrategyChatMessage_PreservesSelectedStrategyAcrossMessagePackRoundTrip()
    {
        var strategy = CreateStrategy("summarize") with
        {
            Body = "Summarize this.",
            SystemPrompt = "Use concise prose.",
            Preprocessors = ["selected-text"]
        };
        ChatMessage message = new UserStrategyChatMessage(
            "argument",
            [],
            strategy,
            new PreprocessorResult
            {
                Variables = new Dictionary<string, string>
                {
                    ["preprocess.selection.text"] = "selected"
                }
            });

        var bytes = MessagePackSerializer.Serialize(message);
        var roundTripped = MessagePackSerializer.Deserialize<ChatMessage>(bytes);

        Assert.That(roundTripped, Is.TypeOf<UserStrategyChatMessage>());
        var strategyMessage = (UserStrategyChatMessage)roundTripped;
        Assert.Multiple(() =>
        {
            Assert.That(strategyMessage.Content, Is.EqualTo("argument"));
            Assert.That(strategyMessage.Strategy.Id, Is.EqualTo("summarize"));
            Assert.That(strategyMessage.Strategy.Body, Is.EqualTo("Summarize this."));
            Assert.That(strategyMessage.Strategy.SystemPrompt, Is.EqualTo("Use concise prose."));
            Assert.That(strategyMessage.Strategy.Preprocessors, Is.EqualTo(new[] { "selected-text" }));
            Assert.That(strategyMessage.Strategy.Includes, Is.Empty);
            Assert.That(strategyMessage.Strategy.Options, Is.EqualTo(StrategyOptions.Default));
            Assert.That(strategyMessage.PreprocessorResult?.Variables?["preprocess.selection.text"], Is.EqualTo("selected"));
        });
    }

    private static Strategy CreateStrategy(string id) => new()
    {
        Id = id,
        NameKey = new DirectResourceKey(id),
    };

    private sealed class TestStrategyProvider(string @namespace, params Strategy[] strategies) : IStrategyProvider
    {
        public string Namespace { get; } = @namespace;

        public IEnumerable<Strategy> GetStrategies() => strategies;
    }
}
