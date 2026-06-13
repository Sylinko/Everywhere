using Everywhere.I18N;
using Everywhere.StrategyEngine;

namespace Everywhere.Core.Tests.StrategyEngine;

public class StrategyPreprocessorExecutorTests
{
    [Test]
    public async Task ExecuteAsync_RunsPreprocessorsInDeclaredOrder_AndLaterVariablesOverride()
    {
        var calls = new List<string>();
        var executor = CreateExecutor(
            new TestPreprocessor("first", _ =>
            {
                calls.Add("first");
                return new PreprocessorResult
                {
                    Variables = new Dictionary<string, string>
                    {
                        ["preprocess.value"] = "one"
                    }
                };
            }),
            new TestPreprocessor("second", _ =>
            {
                calls.Add("second");
                return new PreprocessorResult
                {
                    Variables = new Dictionary<string, string>
                    {
                        ["preprocess.value"] = "two",
                        ["preprocess.other"] = "ok"
                    }
                };
            }));

        var result = await executor.ExecuteAsync(CreateContext(["first", "second"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(calls, Is.EqualTo(new[] { "first", "second" }));
            Assert.That(result.Result.Variables?["preprocess.value"], Is.EqualTo("two"));
            Assert.That(result.Result.Variables?["preprocess.other"], Is.EqualTo("ok"));
            Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("preprocessor.variable_overridden"));
        });
    }

    [Test]
    public async Task ExecuteAsync_UnknownPreprocessor_ReturnsErrorAndStops()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(CreateContext(["missing"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Diagnostics.Single().Code, Is.EqualTo("preprocessor.not_found"));
            Assert.That(result.Result.Variables, Is.Null);
        });
    }

    [Test]
    public async Task ExecuteAsync_PreprocessorException_ReturnsExceptionDiagnostic()
    {
        var executor = CreateExecutor(new TestPreprocessor("boom", _ => throw new InvalidOperationException("nope")));

        var result = await executor.ExecuteAsync(CreateContext(["boom"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Diagnostics.Single().Code, Is.EqualTo("preprocessor.exception"));
            Assert.That(result.Diagnostics.Single().Exception, Is.TypeOf<InvalidOperationException>());
        });
    }

    [Test]
    public async Task ExecuteAsync_PreprocessorTimeout_ReturnsTimeoutDiagnostic()
    {
        var executor = CreateExecutor(new AsyncTestPreprocessor("slow", async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new PreprocessorResult();
        }));

        var context = CreateContext(["slow"]) with
        {
            Strategy = CreateStrategy(["slow"]) with
            {
                Options = StrategyOptions.Default with { PreprocessorTimeout = TimeSpan.FromMilliseconds(10) }
            }
        };

        var result = await executor.ExecuteAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Diagnostics.Single().Code, Is.EqualTo("preprocessor.timeout"));
            Assert.That(result.Diagnostics.Single().Duration, Is.EqualTo(TimeSpan.FromMilliseconds(10)));
        });
    }

    [Test]
    public async Task ExecuteAsync_ErrorDiagnosticFromPreprocessor_StopsBeforeLaterPreprocessors()
    {
        var calls = new List<string>();
        var executor = CreateExecutor(
            new TestPreprocessor("first", _ =>
            {
                calls.Add("first");
                return new PreprocessorResult
                {
                    Diagnostics =
                    [
                        new StrategyDiagnostic
                        {
                            Severity = StrategyDiagnosticSeverity.Error,
                            Code = "preprocessor.context_missing",
                            MessageKey = new DirectResourceKey("missing")
                        }
                    ]
                };
            }),
            new TestPreprocessor("second", _ =>
            {
                calls.Add("second");
                return new PreprocessorResult();
            }));

        var result = await executor.ExecuteAsync(CreateContext(["first", "second"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(calls, Is.EqualTo(new[] { "first" }));
            Assert.That(result.Diagnostics.Single().Code, Is.EqualTo("preprocessor.context_missing"));
        });
    }

    private static StrategyPreprocessorExecutor CreateExecutor(params IStrategyPreprocessor[] preprocessors) =>
        new(new StrategyPreprocessorRegistry(preprocessors));

    private static StrategyExecutionContext CreateContext(IReadOnlyList<string> preprocessors) => new()
    {
        Strategy = CreateStrategy(preprocessors),
        StrategyContext = StrategyContext.FromAttachments([]),
        UserInput = "argument"
    };

    private static Strategy CreateStrategy(IReadOnlyList<string> preprocessors) => new()
    {
        Id = "test",
        NameKey = new DirectResourceKey("test"),
        Preprocessors = preprocessors
    };

    private sealed class TestPreprocessor(string id, Func<StrategyExecutionContext, PreprocessorResult> process)
        : IStrategyPreprocessor
    {
        public string Id { get; } = id;

        public Task<PreprocessorResult> ProcessAsync(
            StrategyExecutionContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(process(context));
    }

    private sealed class AsyncTestPreprocessor(
        string id,
        Func<StrategyExecutionContext, CancellationToken, Task<PreprocessorResult>> process)
        : IStrategyPreprocessor
    {
        public string Id { get; } = id;

        public Task<PreprocessorResult> ProcessAsync(
            StrategyExecutionContext context,
            CancellationToken cancellationToken = default) =>
            process(context, cancellationToken);
    }
}
