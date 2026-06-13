using Everywhere.I18N;
using Everywhere.StrategyEngine;
using Everywhere.StrategyEngine.ConditionExpression;

namespace Everywhere.Core.Tests.StrategyEngine;

public class ConditionExpressionExtraContextTests
{
    [Test]
    public void Compile_InferExtraRootsFromBoundPlan()
    {
        var condition = Compile(
            """
            when:
              extra.file_manager.selection.items:
                count:
                  min: 1
            """);

        Assert.That(condition.Compilation.Requirements.ExtraRoots, Does.Contain("extra.file_manager"));
    }

    [Test]
    public void Evaluate_CollectsNeededExtraRootFromProvider()
    {
        var provider = new StubExtraContextProvider(
            "test.file-manager",
            "extra.file_manager",
            _ => FileManagerRoot("a.md", "b.txt"));
        var condition = Compile(
            """
            when:
              extra.file_manager.selection.items:
                count:
                  min: 2
            """,
            providers: [provider]);

        var result = condition.EvaluateDetailed(StrategyContext.FromAttachments([]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Value, Is.True);
            Assert.That(provider.CollectCount, Is.EqualTo(1));
            Assert.That(provider.LastRequest?.PublicRoot, Is.EqualTo("extra.file_manager"));
            Assert.That(provider.LastRequest?.RequiredPaths, Does.Contain("extra.file_manager.selection.items"));
        });
    }

    [Test]
    public void Evaluate_UsesPrecollectedExtraContextBeforeProvider()
    {
        var provider = new StubExtraContextProvider(
            "test.file-manager",
            "extra.file_manager",
            _ => FileManagerRoot("provider.md"));
        var condition = Compile(
            """
            when:
              extra.file_manager.selection.items:
                contains: precollected.md
            """,
            providers: [provider]);
        var context = new StrategyContext
        {
            Attachments = [],
            ExtraContext = new ExtraContextSnapshot
            {
                Roots = new Dictionary<string, ExtraContextNode>(StringComparer.Ordinal)
                {
                    ["file_manager"] = FileManagerRoot("precollected.md")
                }
            }
        };

        var result = condition.EvaluateDetailed(context);

        Assert.Multiple(() =>
        {
            Assert.That(result.Value, Is.True);
            Assert.That(provider.CollectCount, Is.Zero);
        });
    }

    [Test]
    public void Evaluate_ProviderUnavailableAndTimeoutAreDistinct()
    {
        var unavailable = Compile(
            """
            when:
              extra.file_manager.selection.items:
                count:
                  min: 1
            """,
            providers: []);
        var timeoutProvider = new StubExtraContextProvider(
            "test.slow-file-manager",
            "extra.file_manager",
            async _ =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
                return FileManagerRoot("late.md");
            });
        var timeout = Compile(
            """
            when:
              extra.file_manager.selection.items:
                count:
                  min: 1
            """,
            StrategyOptions.Default with { ExtraTimeout = TimeSpan.FromMilliseconds(10) },
            [timeoutProvider]);

        var unavailableResult = unavailable.EvaluateDetailed(StrategyContext.FromAttachments([]));
        var timeoutResult = timeout.EvaluateDetailed(StrategyContext.FromAttachments([]));

        Assert.Multiple(() =>
        {
            Assert.That(unavailableResult.Value, Is.Null);
            Assert.That(unavailableResult.Diagnostics.Select(d => d.Code), Does.Contain("extra.provider_unavailable"));
            Assert.That(unavailableResult.Diagnostics.Select(d => d.Code), Does.Not.Contain("extra.provider_timeout"));
            Assert.That(timeoutResult.Value, Is.Null);
            Assert.That(timeoutResult.Diagnostics.Select(d => d.Code), Does.Contain("extra.provider_timeout"));
        });
    }

    [Test]
    public void Evaluate_ShortCircuitAvoidsDeferredProvider()
    {
        var provider = new StubExtraContextProvider(
            "test.file-manager",
            "extra.file_manager",
            _ => FileManagerRoot("a.md"));
        var condition = Compile(
            """
            when:
              all:
                - attachments.files:
                    count:
                      min: 1
                - extra.file_manager.selection.items:
                    count:
                      min: 1
            """,
            providers: [provider]);

        var result = condition.EvaluateDetailed(StrategyContext.FromAttachments([]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Value, Is.False);
            Assert.That(provider.CollectCount, Is.Zero);
        });
    }

    private static ExtraContextNode FileManagerRoot(params string[] selectedItems) =>
        new()
        {
            Children = new Dictionary<string, ExtraContextNode>(StringComparer.Ordinal)
            {
                ["selection"] = new ExtraContextNode
                {
                    Children = new Dictionary<string, ExtraContextNode>(StringComparer.Ordinal)
                    {
                        ["items"] = new ExtraContextNode
                        {
                            Value = selectedItems
                        }
                    }
                }
            }
        };

    private static ConditionExpressionCondition Compile(
        string frontmatter,
        StrategyOptions? options = null,
        IReadOnlyList<IExtraContextProvider>? providers = null)
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\dsl.strategy.md",
            $"---\n{frontmatter}\n---\nBody.",
            "user");
        var definition = (StrategyDefinitionV1)document.Definition;
        var diagnostics = new List<StrategyDiagnostic>(document.Diagnostics);
        var condition = new StrategyConditionCompiler(
                options ?? StrategyOptions.Default,
                extraContextProviders: providers)
            .Compile(definition.When!, TestSource, diagnostics) as ConditionExpressionCondition;

        Assert.That(diagnostics.Where(d => d.Severity == StrategyDiagnosticSeverity.Error), Is.Empty);
        Assert.That(condition, Is.Not.Null);
        return condition!;
    }

    private static StrategySource TestSource => new()
    {
        ProviderId = "user",
        Location = new Uri(@"C:\strategies\dsl.strategy.md")
    };

    private sealed class StubExtraContextProvider : IExtraContextProvider
    {
        private readonly Func<ExtraContextRequest, Task<ExtraContextNode?>> _collect;

        public StubExtraContextProvider(
            string id,
            string publicRoot,
            Func<ExtraContextRequest, ExtraContextNode?> collect)
            : this(id, publicRoot, request => Task.FromResult(collect(request)))
        {
        }

        public StubExtraContextProvider(
            string id,
            string publicRoot,
            Func<ExtraContextRequest, Task<ExtraContextNode?>> collect)
        {
            Id = id;
            PublicRoot = publicRoot;
            _collect = collect;
        }

        public string Id { get; }

        public string PublicRoot { get; }

        public IDynamicResourceKey PermissionDescriptionKey { get; } = new DirectResourceKey("test");

        public int CollectCount { get; private set; }

        public ExtraContextRequest? LastRequest { get; private set; }

        public bool CanCollect(StrategyContext baseContext, ExtraContextRequest request) => true;

        public Task<ExtraContextNode?> CollectAsync(
            StrategyContext baseContext,
            ExtraContextRequest request,
            CancellationToken cancellationToken)
        {
            CollectCount++;
            LastRequest = request;
            return _collect(request);
        }
    }
}