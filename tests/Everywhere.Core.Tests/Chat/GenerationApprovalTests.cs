using System.Reflection;
using System.Text.Json;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.BuiltIn;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem;
using Everywhere.Configuration;
using Everywhere.I18N;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using NSubstitute;

namespace Everywhere.Core.Tests.Chat;

public sealed class GenerationApprovalTests
{
    [Test]
    public void GenerationContext_WhenCompressionAdvancesTail_KeepsWritableAssistantAndExecutionBranch()
    {
        using var mixin = CreateMixin();
        using var data = new ChatData();
        data.Add(new UserChatMessage("request", []));
        var assistant = data.Add(new AssistantChatMessage());
        var generation = CreateGeneration(mixin, data);
        var compression = generation.Append(new ContextCompressionChatMessage(data.RootNode.Id, "model", ContextCompressionTrigger.Automatic, null, null));
        Assert.That(generation.CurrentAssistantNode, Is.SameAs(assistant));
        Assert.That(generation.AppendTailNode, Is.SameAs(compression));

        var alternative = data.CreateBranchOn(assistant, new AssistantChatMessage());
        var nextAssistant = generation.Append(new AssistantChatMessage());
        Assert.Multiple(() =>
        {
            Assert.That(nextAssistant.Parent, Is.SameAs(compression));
            Assert.That(generation.CurrentAssistantNode, Is.SameAs(nextAssistant));
            Assert.That(generation.GetPath(), Does.Not.Contain(alternative));
            Assert.That(data.Items[^1], Is.SameAs(alternative));
        });
    }

    [Test]
    public async Task BuildApprovalInput_WithAttachmentsAndStructuredArguments_PreservesExplicitContract()
    {
        using var mixin = CreateMixin();
        using var data = new ChatData();
        await using var runtime = new RootChatContext(data, null);
        data.Add(new UserChatMessage("Run the task", [new TextAttachment(new DirectLocaleKey("source"), "task content 中文")]));
        await using var invocation = CreateInvocation(runtime, CreateGeneration(mixin, data), (_, _, _) => Task.FromResult(ToolApprovalResult.Allow("ok")));
        var arguments = invocation.FunctionCallContent.Arguments ?? throw new AssertionException("Missing arguments.");
        arguments["enabled"] = true;
        arguments["limit"] = 12;
        arguments["optional"] = null;
        using var structure = JsonDocument.Parse("""{ "paths": ["task.py"], "nested": { "safe": false } }""");
        arguments["data"] = structure.RootElement.Clone();
        var method = typeof(ChatService).GetMethod("BuildApprovalInputAsync", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertionException("Missing approval input builder.");
        var paths = new[] { "task.py" };
        var scope = ToolApprovalScope.Create("File operation", new ToolApprovalFileScope(paths, true),
            ToolApprovalInputJsonSerializerContext.ForPrompt.ToolApprovalFileScope);
        paths[0] = "changed.py";
        var inputTask = method.Invoke(null, [invocation, scope, CancellationToken.None]) as Task<string>
            ?? throw new AssertionException("Missing approval input task.");
        var input = await inputTask;
        using var json = JsonDocument.Parse(input);
        var root = json.RootElement;
        var pending = root.GetProperty("PENDING_ACTION").GetProperty("Arguments");
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("EXECUTION_CONSTRAINTS").GetString(), Is.EqualTo("constraints"));
            Assert.That(root.GetProperty("EFFECTIVE_HISTORY")[0].GetProperty("Content").GetString(), Is.EqualTo("Run the task"));
            Assert.That(root.GetProperty("EFFECTIVE_HISTORY")[1].GetProperty("Attachment").GetProperty("Text").GetString(), Is.EqualTo("task content 中文"));
            Assert.That(input, Does.Contain("中文"));
            Assert.That(pending.GetProperty("enabled").GetBoolean(), Is.True);
            Assert.That(pending.GetProperty("limit").GetInt32(), Is.EqualTo(12));
            Assert.That(pending.GetProperty("optional").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(pending.GetProperty("data").GetProperty("nested").GetProperty("safe").GetBoolean(), Is.False);
            var consent = root.GetProperty("CONSENT_SCOPE");
            Assert.That(consent.GetProperty("Operation").GetString(), Is.EqualTo("File operation"));
            Assert.That(consent.GetProperty("Details").GetProperty("Paths")[0].GetString(), Is.EqualTo("task.py"));
            Assert.That(consent.GetProperty("Details").GetProperty("RequiresExplicitApproval").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task RequestConsentAsync_AfterImmediateChange_PreservesEnteredModeAndChangesNextInvocation()
    {
        using var mixin = CreateMixin();
        using var data = new ChatData();
        await using var runtime = new RootChatContext(data, null);
        var generation = CreateGeneration(mixin, data);
        var reviews = 0;
        Task<ToolApprovalResult> ReviewAsync(FunctionCallContext _, ToolApprovalScope? scope, CancellationToken token)
        {
            reviews++;
            return Task.FromResult(ToolApprovalResult.Deny("not authorized"));
        }
        await using var first = CreateInvocation(runtime, generation, ReviewAsync);
        generation.ApprovalState.Mode = ToolApprovalMode.FullAccess;
        await using var next = CreateInvocation(runtime, generation, ReviewAsync);
        var firstDecision = await first.RequestConsentAsync("forced", new DirectLocaleKey("test"));
        var nextDecision = await next.RequestConsentAsync("forced", new DirectLocaleKey("test"));
        Assert.Multiple(() =>
        {
            Assert.That(first.ApprovalMode, Is.EqualTo(ToolApprovalMode.Auto));
            Assert.That(firstDecision.IsAccepted, Is.False);
            Assert.That(nextDecision.IsAccepted, Is.True);
            Assert.That(reviews, Is.EqualTo(1));
            Assert.That(first.FunctionCallChatMessage.IsWaitingForUserInput, Is.False);
        });
    }

    [Test]
    public async Task RequestConsentAsync_WithRememberedScope_DoesNotInvokeReviewer()
    {
        using var mixin = CreateMixin();
        using var data = new ChatData();
        await using var runtime = new RootChatContext(data, null);
        await using var invocation = CreateInvocation(runtime, CreateGeneration(mixin, data), (_, _, _) =>
            throw new AssertionException("Remembered consent must bypass review."));
        runtime.ToolBypassApprovalRulesets[ToolSettingsKey.ForPermission(invocation.ChatPlugin, invocation.ChatFunction, "path")] = true;
        var decision = await invocation.RequestConsentAsync("path", new DirectLocaleKey("test"));
        Assert.That(decision.IsAccepted, Is.True);
    }

    [Test]
    public async Task GenerationContext_WithChild_SharesEnvironmentButNotGeneration()
    {
        using var mixin = CreateMixin();
        using var data = new ChatData();
        await using var runtime = new RootChatContext(data, null);
        var generation = CreateGeneration(mixin, data);
        typeof(ChatContext).GetProperty(nameof(ChatContext.GenerationContext))?.SetValue(runtime, generation);
        var child = runtime.CreateNested("test child");
        generation.ApprovalState.Mode = ToolApprovalMode.FullAccess;
        Assert.Multiple(() =>
        {
            Assert.That(child.InheritedApprovalState, Is.SameAs(generation.ApprovalState));
            Assert.That(child.InheritedApprovalState?.Mode, Is.EqualTo(ToolApprovalMode.FullAccess));
            Assert.That(child.GenerationContext, Is.Null);
            Assert.That(child.WorkingDirectory, Is.EqualTo(((ChatContext)runtime).WorkingDirectory));
            Assert.That(child.UserInterfaceBroker, Is.SameAs(runtime.UserInterfaceBroker));
            Assert.That(child.VisualState, Is.Not.SameAs(runtime.VisualState));
        });
    }

    [Test]
    public async Task EnterFunctionCallContext_WhenSuppressed_RestoresAmbientValue()
    {
        using var mixin = CreateMixin();
        using var data = new ChatData();
        await using var runtime = new RootChatContext(data, null);
        var generation = CreateGeneration(mixin, data);
        await using var first = CreateInvocation(runtime, generation, (_, _, _) => Task.FromResult(ToolApprovalResult.Allow("ok")));
        using (generation.EnterFunctionCallContext(first))
        {
            using (generation.SuppressFunctionCallContext()) Assert.That(generation.FunctionCallContext.Value, Is.Null);
            Assert.That(generation.FunctionCallContext.Value, Is.SameAs(first));
        }
        Assert.That(generation.FunctionCallContext.Value, Is.Null);
    }

    private static KernelMixin CreateMixin() => Substitute.For<KernelMixin>(new AdvancedAssistantConfiguration(),
        new ModelConnection(ModelProviderSchema.OpenAI, "https://example.invalid", null, new HttpClient(), null));

    private static GenerationContext CreateGeneration(KernelMixin mixin, ChatData data) => new(new Kernel(), mixin, Substitute.For<IPromptRenderer>(),
        "constraints", Modalities.Text, 80, -1, new GenerationApprovalState(ToolApprovalMode.Auto), true, data.Items[^1]);

    private static FunctionCallContext CreateInvocation(ChatContext context, GenerationContext generation,
        Func<FunctionCallContext, ToolApprovalScope?, CancellationToken, Task<ToolApprovalResult>> review)
    {
        var plugin = new FileSystemPlugin(new Settings(Substitute.For<IServiceProvider>()), new FileHandlerContextFactory([]),
            Substitute.For<ILogger<FileSystemPlugin>>());
        var function = plugin.GetChatFunctions().First();
        var call = new FunctionCallContent(function.KernelFunction.Name, id: Guid.NewGuid().ToString(), arguments: new KernelArguments());
        return new FunctionCallContext(generation.Kernel, context, plugin, function,
            new FunctionCallChatMessage(LucideIconKind.File, new DirectLocaleKey("test")), call, new ObservableToolRulesets(), generation, review);
    }
}