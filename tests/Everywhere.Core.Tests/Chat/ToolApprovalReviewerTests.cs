using System.Reflection;
using System.Runtime.CompilerServices;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.BuiltIn;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem;
using Everywhere.Collections;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Statistics;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using NSubstitute;
using MEAI = Microsoft.Extensions.AI;

namespace Everywhere.Core.Tests.Chat;

public sealed class ToolApprovalReviewerTests
{
    private readonly List<ReviewHarness> _harnesses = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var harness in _harnesses) harness.Dispose();
        _harnesses.Clear();
    }

    [TestCase("allow", true)]
    [TestCase("deny", false)]
    public async Task ReviewAsync_ThroughChatClientAdapter_AdvertisesFlatNamesAndCompletesReadThenDecision(string decision, bool isAllowed)
    {
        var reviewer = CreateReviewer();
        var reasoning = new MEAI.TextReasoningContent("scope checked") { ProtectedData = "signature" };
        var result = await reviewer.ReviewAsync(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, [reasoning, Read()]), Response(Decision(decision)));
        Assert.Multiple(() =>
        {
            Assert.That(result.Failure, Is.Null);
            Assert.That(result.IsAllowed, Is.EqualTo(isAllowed));
            Assert.That(result.Reason, Is.EqualTo("scope checked"));
            Assert.That(reviewer.Handler.ReadCount, Is.EqualTo(1));
            Assert.That(reviewer.Requests, Has.Count.EqualTo(2));
            foreach (var names in reviewer.AdvertisedNames) Assert.That(names, Is.EquivalentTo(new[] { "read_file", "submit_approval" }));
            var readCall = reviewer.Requests[1].SelectMany(message => message.Contents).OfType<MEAI.FunctionCallContent>().Single();
            var readResult = reviewer.Requests[1].SelectMany(message => message.Contents).OfType<MEAI.FunctionResultContent>().Single();
            Assert.That(readCall.Name, Is.EqualTo("read_file"));
            Assert.That(readResult.CallId, Is.EqualTo(readCall.CallId));
            Assert.That(readResult.Result?.ToString(), Does.Contain("print('hello')"));
            var historyReasoning = reviewer.Requests[1].SelectMany(message => message.Contents).OfType<MEAI.TextReasoningContent>().Single();
            Assert.That(historyReasoning.Text, Is.EqualTo("scope checked"));
            Assert.That(historyReasoning.ProtectedData, Is.EqualTo("signature"));
        });
    }

    [Test]
    public async Task ReviewAsync_WithReadsBetweenProse_RemindsTwiceThenFails()
    {
        var reviewer = CreateReviewer();
        var result = await reviewer.ReviewAsync(
            new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "pending"), Response(Read()),
            new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "pending"), Response(Read()), new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "pending"));
        Assert.Multiple(() =>
        {
            Assert.That(reviewer.Handler.ReadCount, Is.EqualTo(2));
            Assert.That(reviewer.Requests, Has.Count.EqualTo(5));
            Assert.That(reviewer.Requests[^1].Count(message => message.Role == MEAI.ChatRole.User) - 1, Is.EqualTo(2));
            Assert.That(result.Failure, Is.EqualTo(ToolApprovalFailure.DecisionMissing));
            Assert.That(result.IsAllowed, Is.False);
        });
    }

    [TestCase(4, null)]
    [TestCase(5, ToolApprovalFailure.ReadLimitExceeded)]
    public async Task ReviewAsync_WithReadBudget_ReadsThreeThenNotifiesAndFailsOnFifth(int readBatches, ToolApprovalFailure? failure)
    {
        var reviewer = CreateReviewer();
        var responses = Enumerable.Range(0, readBatches)
            .Select(index => index == 4 ? Response(Decision(), Read()) : Response(Read())).Append(Response(Decision())).ToArray();
        var result = await reviewer.ReviewAsync(responses);
        Assert.Multiple(() =>
        {
            Assert.That(reviewer.Handler.ReadCount, Is.EqualTo(3));
            Assert.That(result.Failure, Is.EqualTo(failure));
            Assert.That(result.IsAllowed, Is.EqualTo(failure is null));
            Assert.That(reviewer.Requests, Has.Count.EqualTo(5));
            Assert.That(reviewer.ToolResults.Any(content => content.Result?.ToString()?.Contains("No files were read") is true), Is.True);
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ReviewAsync_WithParallelReads_CountsOneBatchAndDefersMixedDecision(bool isDecisionFirst)
    {
        var reviewer = CreateReviewer();
        var mixedResponse = isDecisionFirst ?
            Response(Decision("deny"), Read(), Read(), Read(), Read()) :
            Response(Read(), Read(), Read(), Read(), Decision("deny"));
        var result = await reviewer.ReviewAsync(
            mixedResponse, Response(Read()), Response(Read()), Response(Decision()));
        Assert.Multiple(() =>
        {
            Assert.That(reviewer.Handler.ReadCount, Is.EqualTo(6));
            Assert.That(result.IsAllowed, Is.True);
            Assert.That(reviewer.ToolResults.Any(content => content.Result?.ToString()?.StartsWith("Decision deferred") is true), Is.True);
        });
    }

    [Test]
    public async Task ReviewAsync_WithReadFailure_ReturnsEvidenceAndStillRequiresDecision()
    {
        var reviewer = CreateReviewer();
        reviewer.Handler.ShouldFail = true;
        var result = await reviewer.ReviewAsync(Response(Read()), Response(Decision("deny")));
        Assert.Multiple(() =>
        {
            Assert.That(reviewer.Handler.ReadCount, Is.EqualTo(1));
            Assert.That(result.Failure, Is.Null);
            Assert.That(result.IsAllowed, Is.False);
            Assert.That(reviewer.ToolResults.Any(content => content.Result?.ToString()?.StartsWith("File read failed") is true), Is.True);
        });
    }

    [Test]
    public async Task ReviewAsync_WithConflictingDecisions_FailsWithoutApproving()
    {
        var reviewer = CreateReviewer();
        var result = await reviewer.ReviewAsync(Response(Read(), Decision(), Decision("deny")));
        Assert.Multiple(() =>
        {
            Assert.That(result.Failure, Is.EqualTo(ToolApprovalFailure.InvalidResponse));
            Assert.That(result.IsAllowed, Is.False);
            Assert.That(reviewer.Handler.ReadCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ReviewAsync_WithUnknownToolAfterDecision_FailsWithoutApproving()
    {
        var reviewer = CreateReviewer();
        var unknownCall = new MEAI.FunctionCallContent(Guid.NewGuid().ToString(), "unknown_tool", new Dictionary<string, object?>());
        var result = await reviewer.ReviewAsync(Response(Decision(), unknownCall));
        Assert.Multiple(() =>
        {
            Assert.That(result.Failure, Is.EqualTo(ToolApprovalFailure.InvalidResponse));
            Assert.That(result.IsAllowed, Is.False);
            Assert.That(reviewer.Requests, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ReviewAsync_WithIncompleteDecision_Fails()
    {
        var reviewer = CreateReviewer();
        var result = await reviewer.ReviewResponsesAsync(new MEAI.ChatResponse(Response(Decision())) { FinishReason = MEAI.ChatFinishReason.Length });
        Assert.Multiple(() =>
        {
            Assert.That(result.Failure, Is.EqualTo(ToolApprovalFailure.InvalidResponse));
            Assert.That(result.IsAllowed, Is.False);
        });
    }

    [Test]
    public async Task ReviewAsync_WithStreamFailureAfterDecision_FailsWithoutApproving()
    {
        var reviewer = CreateReviewer();
        reviewer.StreamFailure = new IOException("Stream disconnected.");
        var result = await reviewer.ReviewAsync(Response(Decision()));
        Assert.Multiple(() =>
        {
            Assert.That(result.Failure, Is.EqualTo(ToolApprovalFailure.ProviderError));
            Assert.That(result.IsAllowed, Is.False);
        });
    }

    private static MEAI.ChatMessage Response(params MEAI.FunctionCallContent[] calls) => new(MEAI.ChatRole.Assistant, [.. calls]);

    private static MEAI.FunctionCallContent Read() => new(Guid.NewGuid().ToString(), "read_file",
        new Dictionary<string, object?> { ["path"] = "task.py" });

    private static MEAI.FunctionCallContent Decision(string decision = "allow") => new(Guid.NewGuid().ToString(), "submit_approval",
        new Dictionary<string, object?> { ["decision"] = decision, ["reason"] = "scope checked" });

    private ReviewHarness CreateReviewer()
    {
        var reviewer = new ReviewHarness();
        _harnesses.Add(reviewer);
        return reviewer;
    }

    private sealed class ReviewHarness : IDisposable
    {
        public CountingFileHandler Handler { get; } = new();
        public List<MEAI.ChatMessage[]> Requests { get; } = [];
        public List<string[]> AdvertisedNames { get; } = [];
        public Exception? StreamFailure { get; set; }
        public IEnumerable<MEAI.FunctionResultContent> ToolResults => Requests[^1].SelectMany(message => message.Contents).OfType<MEAI.FunctionResultContent>();

        private readonly MEAI.IChatClient _client = Substitute.For<MEAI.IChatClient>();
        private readonly KernelMixin _mixin;
        private readonly ChatService _owner;
        private readonly FunctionCallContext _context;

        public ReviewHarness()
        {
            _client.GetService(Arg.Any<Type>(), Arg.Any<object?>()).Returns((object?)null);
            _client.GetResponseAsync(Arg.Any<IEnumerable<MEAI.ChatMessage>>(), Arg.Any<MEAI.ChatOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<MEAI.ChatResponse>(new AssertionException("Approval must use the streaming API.")));
            var completion = _client.AsChatCompletionService();
            _mixin = Substitute.For<KernelMixin>(new AdvancedAssistantConfiguration { SupportsToolCall = true },
                new ModelConnection(ModelProviderSchema.OpenAI, "https://example.invalid", null, new HttpClient(), null));
            _mixin.ChatCompletionService.Returns(completion);
            _mixin.GetPromptExecutionSettings(Arg.Any<FunctionChoiceBehavior>()).Returns(call =>
                new PromptExecutionSettings { FunctionChoiceBehavior = call.ArgAt<FunctionChoiceBehavior>(0) });
            var settings = new Settings(Substitute.For<IServiceProvider>());
            var plugin = new FileSystemPlugin(settings, new FileHandlerContextFactory([Handler]), Substitute.For<ILogger<FileSystemPlugin>>());
            var manager = Substitute.For<IChatPluginManager>();
            manager.BuiltInPlugins.Returns(new BindableList<BuiltInChatPlugin> { plugin });
            // Use the production constructor, substituting only services outside the review path.
            var constructor = typeof(ChatService).GetConstructors().Single();
            _owner = (ChatService)constructor.Invoke(constructor.GetParameters().Select(parameter => parameter.ParameterType switch
            {
                var type when type == typeof(Settings) => (object)settings,
                var type when type == typeof(IChatPluginManager) => manager,
                var type when type == typeof(IStatisticsRecorder) => Substitute.For<IStatisticsRecorder>(),
                var type when type == typeof(ILogger<ChatService>) => Substitute.For<ILogger<ChatService>>(),
                _ => null
            }).ToArray());
            var generation = new GenerationContext(new Kernel(), _mixin, Substitute.For<IPromptRenderer>(), "constraints",
                Modalities.Text, 80, -1, new GenerationApprovalState(ToolApprovalMode.Auto), true);
            var function = plugin.GetChatFunctions().First();
            _context = new FunctionCallContext(generation.Kernel, new ChatContext(), plugin, function,
                new FunctionCallChatMessage(LucideIconKind.File, new DirectLocaleKey("test")),
                new FunctionCallContent(function.KernelFunction.Name, id: Guid.NewGuid().ToString(), arguments: new KernelArguments()),
                new ObservableToolRulesets(), generation, InvokeReviewAsync);
        }

        public Task<ToolApprovalResult> ReviewAsync(params MEAI.ChatMessage[] responses) =>
            ReviewResponsesAsync(responses.Select(response => new MEAI.ChatResponse(response)).ToArray());

        public Task<ToolApprovalResult> ReviewResponsesAsync(params MEAI.ChatResponse[] responses)
        {
            var pending = new Queue<MEAI.ChatResponse>(responses);
            _client.GetStreamingResponseAsync(Arg.Any<IEnumerable<MEAI.ChatMessage>>(), Arg.Any<MEAI.ChatOptions>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Requests.Add(call.ArgAt<IEnumerable<MEAI.ChatMessage>>(0).ToArray());
                    var options = call.ArgAt<MEAI.ChatOptions>(1);
                    AdvertisedNames.Add(options.Tools?.OfType<MEAI.AIFunction>().Select(function => function.Name).ToArray() ?? []);
                    return StreamAsync(pending.Dequeue());
                });
            return InvokeReviewAsync(_context, null, CancellationToken.None);
        }

        private async IAsyncEnumerable<MEAI.ChatResponseUpdate> StreamAsync(MEAI.ChatResponse response, [EnumeratorCancellation] CancellationToken token = default)
        {
            foreach (var message in response.Messages)
            {
                foreach (var content in message.Contents)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Yield();
                    yield return new MEAI.ChatResponseUpdate { Role = message.Role, Contents = [content] };
                }
            }
            if (StreamFailure is { } failure) throw failure;
            yield return new MEAI.ChatResponseUpdate
            {
                Role = MEAI.ChatRole.Assistant,
                FinishReason = response.FinishReason ?? MEAI.ChatFinishReason.ToolCalls,
                Contents = [new MEAI.UsageContent(new MEAI.UsageDetails { InputTokenCount = 12 })]
            };
        }

        private Task<ToolApprovalResult> InvokeReviewAsync(FunctionCallContext context, ToolApprovalScope? scope, CancellationToken token)
        {
            var method = typeof(ChatService).GetMethod("ReviewToolApprovalAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing production approval entry point.");
            return method.Invoke(_owner, [context, scope, token]) as Task<ToolApprovalResult>
                ?? throw new AssertionException("Missing approval task.");
        }

        public void Dispose()
        {
            _context.Dispose();
            _context.ChatContext.Dispose();
            _mixin.Dispose();
            _client.Dispose();
        }
    }

    private sealed class CountingFileHandler : FileHandler
    {
        public int ReadCount { get; private set; }
        public bool ShouldFail { get; set; }

        public override ValueTask<FileHandlerContext?> TryCreateContextAsync(string path, string workingDirectory, CancellationToken cancellationToken) =>
            ValueTask.FromResult<FileHandlerContext?>(new FileHandlerContext(this, path, workingDirectory));

        public override ValueTask<FileReadResult> ReadAsync(FileHandlerContext context, int offset, int limit, CancellationToken cancellationToken)
        {
            ReadCount++;
            if (ShouldFail) throw new IOException("missing file");
            return ValueTask.FromResult(new FileReadResult { Items = [new("print('hello')")], Offset = offset, Unit = "line", HasMore = false });
        }
    }
}
