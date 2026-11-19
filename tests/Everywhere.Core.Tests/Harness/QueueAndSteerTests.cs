using System.IO.Pipes;
using System.Net;
using System.Reactive.Disposables;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using DynamicData;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Automation;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.BuiltIn;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Core.Tests.Chat;
using Everywhere.Core.Tests.Statistics;
using Everywhere.Database;
using Everywhere.Harness;
using Everywhere.I18N;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.ProcessIsolation.Watchdog;
using Everywhere.Skills;
using Everywhere.Statistics;
using Everywhere.Storage;
using Everywhere.Views;
using Lucide.Avalonia;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Everywhere.Core.Tests.Harness;

/// <summary>
/// Covers busy submission queueing, mid-turn steering, opt-in backgroundable tools,
/// hidden background job management, and approval dialog independence.
/// </summary>
public sealed class QueueAndSteerTests
{
    [AvaloniaTest]
    public async Task SubmitMessage_WhenBusyAndQueued_DrainsSequentiallyWithPreservedPrefix()
    {
        using var harness = new TestChatHarness();
        var root = harness.Root;
        var turn1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowTurn1Finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn2Finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.ConfigureHandler(async (requestCount, _, token) =>
        {
            if (requestCount == 1)
            {
                turn1Started.TrySetResult();
                await allowTurn1Finish.Task.WaitAsync(token);
                return CreateTextSseResponse("Reply 1");
            }

            return CreateTextSseResponse("Reply 2");
        });

        root.ForegroundOperationCompleted += ctx =>
        {
            if (!ctx.IsBusy && root.Data.SelectedNodes.Count >= 5)
            {
                turn2Finished.TrySetResult();
            }
        };

        var accepted1 = await harness.ChatService.SubmitMessageAsync(
            root,
            new UserChatMessage("Turn 1", []),
            harness.Assistant,
            ChatInputDeliveryMode.Queue);
        Assert.That(accepted1, Is.True);

        await turn1Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var diagnostics = string.Join(" | ", root.Data.SelectedNodes.Select(n => $"{n.Message.Kind}:{n.Message.ToString()}:Err={((n.Message as AssistantChatMessage)?.Error?.Message ?? "none")}"));
        Assert.That(root.IsBusy, Is.True, $"Diagnostics: {diagnostics}; Logs: {string.Join(" ; ", harness.Logs)}");

        var accepted2 = await harness.ChatService.SubmitMessageAsync(
            root,
            new UserChatMessage("Turn 2", []),
            harness.Assistant,
            ChatInputDeliveryMode.Queue);
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(accepted2, Is.True);
            Assert.That(root.PendingInputs.WaitingCount, Is.EqualTo(1));
            Assert.That(root.PendingInputs.WaitingItems, Has.Count.EqualTo(1));
            Assert.That(root.PendingInputs.WaitingItems[0].DeliveryMode, Is.EqualTo(ChatInputDeliveryMode.Queue));
            // Queued messages must not pollute ChatData until drained (Root + User1 + Assistant1 = 3).
            Assert.That(root.Data.SelectedNodes.Count, Is.EqualTo(3));
        });

        allowTurn1Finish.TrySetResult();
        await turn2Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();

        var nodes = root.Data.SelectedNodes;
        Assert.Multiple(() =>
        {
            Assert.That(root.PendingInputs.WaitingCount, Is.EqualTo(0));
            Assert.That(root.IsBusy, Is.False);
            Assert.That(nodes, Has.Count.EqualTo(5));
            Assert.That(((UserChatMessage)nodes[1].Message).Content, Is.EqualTo("Turn 1"));
            Assert.That(((UserChatMessage)nodes[1].Message).DeliveryMode, Is.Null);
            Assert.That(nodes[2].Message.ToString(), Is.EqualTo("Reply 1"));
            Assert.That(((UserChatMessage)nodes[3].Message).Content, Is.EqualTo("Turn 2"));
            Assert.That(((UserChatMessage)nodes[3].Message).DeliveryMode, Is.EqualTo(ChatInputDeliveryMode.Queue));
            Assert.That(nodes[4].Message.ToString(), Is.EqualTo("Reply 2"));

            // Prefix check: system instruction remains identical across Turn 1 and Turn 2 requests.
            Assert.That(harness.Requests, Has.Count.EqualTo(2));
            Assert.That(
                harness.Requests[0].GetProperty("system_instruction").GetString(),
                Is.EqualTo(harness.Requests[1].GetProperty("system_instruction").GetString()));
        });
    }

    [AvaloniaTest]
    public async Task Cancel_WhenQueuedItemsExist_PausesAutoDrainAndPreservesItemsUntilResumed()
    {
        using var harness = new TestChatHarness();
        var root = harness.Root;
        var turn1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foregroundEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.ConfigureHandler(async (requestCount, _, token) =>
        {
            if (requestCount == 1)
            {
                turn1Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return CreateTextSseResponse($"Resumed reply {requestCount}");
        });

        var endedCount = 0;
        root.ForegroundOperationCompleted += ctx =>
        {
            endedCount++;
            if (endedCount == 1)
            {
                foregroundEnded.TrySetResult();
            }
            else if (!ctx.IsBusy && ctx.PendingInputs.WaitingCount == 0)
            {
                resumedFinished.TrySetResult();
            }
        };

        await harness.ChatService.SubmitMessageAsync(
            root,
            new UserChatMessage("Initial", []),
            harness.Assistant,
            ChatInputDeliveryMode.Queue);
        await turn1Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await harness.ChatService.SubmitMessageAsync(
            root,
            new UserChatMessage("Queued after cancel", []),
            harness.Assistant,
            ChatInputDeliveryMode.Queue);

        root.Cancel();
        await foregroundEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(root.IsBusy, Is.False);
            Assert.That(root.PendingInputs.IsDrainPaused, Is.True);
            Assert.That(root.PendingInputs.WaitingCount, Is.EqualTo(1));
            Assert.That(harness.Requests, Has.Count.EqualTo(1));
        });

        Assert.That(harness.ChatService.ResumePendingInputs(root), Is.True);
        await resumedFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(root.PendingInputs.IsDrainPaused, Is.False);
            Assert.That(root.PendingInputs.WaitingCount, Is.EqualTo(0));
            Assert.That(harness.Requests, Has.Count.EqualTo(2));
            Assert.That(root.Data.SelectedNodes.OfType<ChatMessageNode>().Count(n => n.Message is UserChatMessage), Is.EqualTo(2));
        });
    }

    [AvaloniaTest]
    public async Task Steer_WhenBackgroundableAndSerialToolsAreBatched_DetachesRunningSkipsUnstartedAndSuppressesNoResponse()
    {
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowToolComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serialToolCalls = 0;
        var foregroundTurnDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wakeTurnDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var testPlugin = new SteerTestPlugin(
            onBackgroundTool: async ct =>
            {
                toolStarted.TrySetResult();
                await allowToolComplete.Task.WaitAsync(ct);
                return "background finished value";
            },
            onSerialTool: () =>
            {
                serialToolCalls++;
                return "should never run";
            });

        using var harness = new TestChatHarness(testPlugin);
        var root = harness.Root;

        harness.ConfigureHandler((requestCount, _, _) =>
        {
            return Task.FromResult(requestCount switch
            {
                1 => CreateToolBatchSseResponse(
                    ("call-bg", "bg_probe", """{"title":"long task"}"""),
                    ("call-serial", "serial_probe", "{}")),
                2 => CreateTextSseResponse("Handled steer while bg runs."),
                _ => CreateTextSseResponse("Acknowledged background result.")
            });
        });

        var completions = 0;
        root.ForegroundOperationCompleted += ctx =>
        {
            completions++;
            if (completions == 1)
            {
                foregroundTurnDone.TrySetResult();
            }
            else if (completions >= 2)
            {
                wakeTurnDone.TrySetResult();
            }
        };

        await harness.ChatService.SubmitMessageAsync(
            root,
            new UserChatMessage("Run work", []),
            harness.Assistant,
            ChatInputDeliveryMode.Queue);

        await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Send a Steer while bg_probe is running and serial_probe is waiting behind it.
        await harness.ChatService.SubmitMessageAsync(
            root,
            new UserChatMessage("Switch topic", []),
            harness.Assistant,
            ChatInputDeliveryMode.Steer);

        await foregroundTurnDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();

        var nodesDuringBg = root.Data.SelectedNodes.ToArray();
        var firstAssistant = nodesDuringBg.Select(static n => n.Message).OfType<AssistantChatMessage>().First();
        var functionSpan = firstAssistant.Items.OfType<AssistantChatMessageFunctionCallSpan>().Single();
        var bgMessage = functionSpan.FunctionCalls[0];
        var skippedMessage = functionSpan.FunctionCalls[1];

        Assert.Multiple(() =>
        {
            // serial_probe was skipped and paired cleanly with a synthetic skipped result.
            Assert.That(serialToolCalls, Is.EqualTo(0));
            Assert.That(bgMessage.IsBusy, Is.False);
            Assert.That(bgMessage.Results[0].Result?.ToString(), Does.Contain("bg-1"));
            Assert.That(skippedMessage.IsBusy, Is.False);
            Assert.That(skippedMessage.Results[0].Result?.ToString(), Does.Contain("steering"));

            // Steer user message & reply already committed while background job stays alive.
            Assert.That(root.BackgroundJobs.ActiveJobs, Has.Count.EqualTo(1));
            Assert.That(root.IsBusy, Is.False);
            Assert.That(root.HasWork, Is.True);
            Assert.That(nodesDuringBg.Any(n => n.Message is UserChatMessage { Content: "Switch topic", DeliveryMode: ChatInputDeliveryMode.Steer }), Is.True);
            Assert.That(nodesDuringBg[^1].Message.ToString(), Is.EqualTo("Handled steer while bg runs."));
        });

        // Presentation check: tool-only firstAssistant with steered follow-up must NOT show NoResponse row.
        var presentation = root.Presentation;
        Dispatcher.UIThread.RunJobs();
        Assert.That(presentation.Rows.OfType<NoResponsePresentationRow>(), Is.Empty);

        // Now let the detached background job complete -> session wakes automatically cleanly.
        allowToolComplete.TrySetResult();
        await wakeTurnDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();

        var finalNodes = root.Data.SelectedNodes.ToArray();
        var wakeDiag = string.Join(" | ", finalNodes.Select(n => $"{n.Message.Kind}:{n.Message.ToString()}:Err={((n.Message as AssistantChatMessage)?.Error?.Message ?? ((n.Message as AssistantChatMessage)?.ErrorMessageKey?.ToString() ?? "none"))}"));
        Assert.Multiple(() =>
        {
            Assert.That(root.BackgroundJobs.ActiveJobs, Is.Empty);
            Assert.That(finalNodes.OfType<ChatMessageNode>().Any(n => n.Message is BackgroundJobNotificationChatMessage), Is.True);
            var notification = finalNodes.Select(n => n.Message).OfType<BackgroundJobNotificationChatMessage>().Single();
            Assert.That(notification.JobId, Is.EqualTo("bg-1"));
            Assert.That(notification.Label, Is.EqualTo("long task"));
            Assert.That(notification.IsSuccess, Is.True);
            Assert.That(notification.Output, Is.EqualTo("background finished value"));
            Assert.That(finalNodes[^1].Message.ToString(), Is.EqualTo("Acknowledged background result."), $"Diag: {wakeDiag}; Logs: {string.Join(" ; ", harness.Logs)}");
        });
    }

    [AvaloniaTest]
    public async Task ApprovalDialogs_WhenToolDetachesAndForegroundEnds_HumanConsentAndAutoReviewRemainWorking()
    {
        // Part A: Human consent stays open after moving tool to background and completing foreground turn.
        var consentRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foregroundTurnDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backgroundWakeDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var testPlugin = new SteerTestPlugin(
            onConsentTool: async (ui, ct) =>
            {
                consentRequested.TrySetResult();
                var result = await ui.RequestConsentAsync(
                    "custom.perm",
                    new DirectLocaleKey("Confirm background permission"),
                    cancellationToken: ct);
                return result.IsAccepted ? "consent accepted" : "consent denied";
            });

        using (var harness = new TestChatHarness(testPlugin, ToolApprovalMode.Ask))
        {
            var root = harness.Root;
            harness.ConfigureHandler((requestCount, _, _) => Task.FromResult(requestCount switch
            {
                1 => CreateToolBatchSseResponse(("call-consent", "consent_probe", "{}")),
                2 => CreateTextSseResponse("Moved consent tool to background."),
                _ => CreateTextSseResponse("Background tool got approval.")
            }));

            var completions = 0;
            root.ForegroundOperationCompleted += _ =>
            {
                completions++;
                if (completions == 1) foregroundTurnDone.TrySetResult();
                else if (completions >= 2) backgroundWakeDone.TrySetResult();
            };

            await harness.ChatService.SubmitMessageAsync(
                root,
                new UserChatMessage("Run consent tool", []),
                harness.Assistant,
                ChatInputDeliveryMode.Queue);

            await WaitForConsentItemAsync(root, TimeSpan.FromSeconds(5));
            Assert.That(root.UserInterfaceBroker.ChatPluginUserInterfaceItems.OfType<ChatPluginUserInterfaceConsentRequestItem>().Count(), Is.EqualTo(1));

            // Steer detaches consent_probe to background and finishes foreground turn while outer consent dialog stays open.
            await harness.ChatService.SubmitMessageAsync(
                root,
                new UserChatMessage("Continue in background", []),
                harness.Assistant,
                ChatInputDeliveryMode.Steer);

            await foregroundTurnDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            // Human consent item on SharedUserInterfaceBroker must STILL be open and NOT canceled!
            var openConsent = root.UserInterfaceBroker.ChatPluginUserInterfaceItems
                .OfType<ChatPluginUserInterfaceConsentRequestItem>()
                .Single();
            openConsent.Command.Execute(ConsentDecision.AllowOnce);

            // Inner tool consent also arrives via consent_probe's RequestConsentAsync; pump dispatcher until item appears.
            await consentRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var innerConsent = await WaitForConsentItemAsync(root, TimeSpan.FromSeconds(5));
            innerConsent.Command.Execute(ConsentDecision.AllowOnce);

            await backgroundWakeDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            var notification = root.Data.SelectedNodes
                .Select(static n => n.Message)
                .OfType<BackgroundJobNotificationChatMessage>()
                .Single();
            Assert.Multiple(() =>
            {
                Assert.That(notification.IsSuccess, Is.True);
                Assert.That(notification.Output, Is.EqualTo("consent accepted"));
            });
        }

        // Part B: AI Auto approval succeeds even after the originating foreground GenerationContext is disposed.
        using var autoHarness = new TestChatHarness(approvalMode: ToolApprovalMode.Auto);
        autoHarness.Settings.SystemAssistant.ToolApproval.AutoSelect = true;
        autoHarness.ConfigureHandler((_, _, _) => Task.FromResult(CreateApprovalDecisionSseResponse("allow")));

        var userNode = autoHarness.Root.Data.StartTurn(new UserChatMessage("Need approval", []));
        var assistantNode = autoHarness.Root.Data.Append(userNode, new AssistantChatMessage { IsBusy = true });

        var generationMethod = typeof(ChatService).GetMethod(
            "CreateGenerationContextAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var generation = await (Task<GenerationContext>)generationMethod.Invoke(
            autoHarness.ChatService,
            [autoHarness.Root, assistantNode, autoHarness.Assistant, null, CancellationToken.None, true])!;

        var approvalMethod = typeof(ChatService).GetMethod(
            "ReviewToolApprovalAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Func<FunctionCallContext, ToolApprovalScope?, CancellationToken, Task<ToolApprovalResult>> reviewDelegate =
            (ctx, scope, ct) => (Task<ToolApprovalResult>)approvalMethod.Invoke(autoHarness.ChatService, [ctx, scope, ct])!;

        var functionCallMessage = new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey("probe"));
        var callContent = new Microsoft.Extensions.AI.FunctionCallContent("call-auto", "auto_probe");
        var binding = new ToolBinding(
            autoHarness.Plugins.NativePlugins.First(),
            autoHarness.Plugins.NativePlugins.First().Functions.First(),
            new());

        await using var functionContext = new FunctionCallContext(
            generation.Services,
            autoHarness.Root,
            binding,
            functionCallMessage,
            callContent,
            autoHarness.Settings.Plugin.ToolBypassApprovalRulesets,
            generation,
            reviewDelegate,
            supportsBackgrounding: true);

        Assert.That(functionContext.TryRequestMoveToBackground(), Is.True);
        await generation.DisposeAsync();
        Assert.That(generation.IsDisposed, Is.True);

        var decision = await functionContext.RequestConsentAsync(
            "auto.perm",
            new DirectLocaleKey("Review auto permission"),
            cancellationToken: CancellationToken.None);
        Assert.That(decision.IsAccepted, Is.True);
    }

    [AvaloniaTest]
    public async Task BuiltInPlugins_SerializationAndManageTool_MatchContracts()
    {
        // 1. BackgroundJobNotificationChatMessage MessagePack round-trip & CanStartTurn == false
        var notification = new BackgroundJobNotificationChatMessage(
            "bg-7",
            "execute_in_terminal",
            "npm test",
            true,
            "All passed",
            null,
            new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero));
        var userSteer = new UserChatMessage("Redirect now", []) { DeliveryMode = ChatInputDeliveryMode.Steer };

        var roundTrippedNote = (BackgroundJobNotificationChatMessage)MessagePackSerializer.Deserialize<ChatMessage>(
            MessagePackSerializer.Serialize<ChatMessage>(notification, MessagePackSerializerOptions.Standard),
            MessagePackSerializerOptions.Standard);
        var roundTrippedSteer = (UserChatMessage)MessagePackSerializer.Deserialize<ChatMessage>(
            MessagePackSerializer.Serialize<ChatMessage>(userSteer, MessagePackSerializerOptions.Standard),
            MessagePackSerializerOptions.Standard);

        Assert.Multiple(() =>
        {
            Assert.That(roundTrippedNote.Kind, Is.EqualTo(ChatMessageKind.BackgroundJobNotification));
            Assert.That(ChatData.CanStartTurn(roundTrippedNote.Kind), Is.False);
            Assert.That(roundTrippedNote.JobId, Is.EqualTo("bg-7"));
            Assert.That(roundTrippedNote.Output, Is.EqualTo("All passed"));
            Assert.That(roundTrippedSteer.DeliveryMode, Is.EqualTo(ChatInputDeliveryMode.Steer));
        });

        // 2. ChatHistoryBuilder ResolveStartIndex ignores BackgroundJobNotificationChatMessage when counting MaxContextRounds
        using var historyData = new ChatData();
        await using var historyRoot = new RootChatContext(historyData, null);
        var u0 = historyData.StartTurn(new UserChatMessage("Round 0", []));
        var a0Msg = new AssistantChatMessage();
        a0Msg.AddSpan(new AssistantChatMessageTextSpan("Answer 0"));
        var a0 = historyData.Append(u0, a0Msg);
        var u1 = historyData.StartTurn(a0, new UserChatMessage("Round 1", []));
        var a1Msg = new AssistantChatMessage();
        a1Msg.AddSpan(new AssistantChatMessageTextSpan("Answer 1"));
        var a1 = historyData.Append(u1, a1Msg);
        var bgNote = historyData.Append(a1, notification);
        var a1FollowMsg = new AssistantChatMessage();
        a1FollowMsg.AddSpan(new AssistantChatMessageTextSpan("Noted bg-7"));
        var a1Follow = historyData.Append(bgNote, a1FollowMsg);
        var u2 = historyData.StartTurn(a1Follow, new UserChatMessage("Round 2", []));
        var a2 = historyData.Append(u2, new AssistantChatMessage { IsBusy = true });

        var selection = await ChatHistoryBuilder.SelectContextMessagesAsync(
            historyRoot,
            historyData.SelectedNodes,
            1,
            0,
            CancellationToken.None);
        Assert.That(selection.Messages.Any(m => m.ToString()?.Contains("Round 2") is true), Is.True);
        Assert.That(selection.Messages.Any(m => m.ToString()?.Contains("Round 1") is true), Is.True);
        Assert.That(selection.Messages.Any(m => m.ToString()?.Contains("Round 0") is true), Is.False);

        // 3. Built-in plugin capabilities: SupportsBackgrounding & hidden manage_background_jobs
        var services = Substitute.For<IServiceProvider>();
        var settings = new Settings(services);
        services.GetService(typeof(Settings)).Returns(settings);
        var essential = new EssentialPlugin(
            settings,
            new AssistantSpecializationResolver(
                new AssistantCatalog(Substitute.For<IPresetModelProvider>(), Substitute.For<IOfficialModelProvider>())),
            Substitute.For<IBlobStorage>());
        var terminal = new TerminalPlugin(
            settings,
            Substitute.For<IWatchdogManager>(),
            NullLogger<TerminalPlugin>.Instance);
        settings.Plugin.ToolEnablementRulesets[ToolSettingsKey.ForPlugin(terminal)] = true;

        var pluginManager = new ChatPluginManager(
            services,
            [essential, terminal],
            settings,
            Substitute.For<IRuntimeManager>(),
            NullLogger<ChatPluginManager>.Instance);
        using var releasePluginManager = Disposable.Create(pluginManager.Dispose);

        var scope = await pluginManager.CreateScopeAsync(
            new AdvancedAssistantConfiguration { ModelId = "test", SupportsToolCall = true },
            historyRoot,
            a2,
            new ToolRulesetsPipeline([]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            // UI-filtered Functions hides manage_background_jobs while Scope still exposes it to the model.
            Assert.That(essential.Functions.Any(f => f.Function.Name == "manage_background_jobs"), Is.False);
            Assert.That(scope.TryGetBinding("run_subagent", out var runSubagentBinding, out _), Is.True);
            Assert.That(runSubagentBinding!.Function.SupportsBackgrounding, Is.True);
            Assert.That(scope.TryGetBinding("execute_in_terminal", out var terminalBinding, out _), Is.True);
            Assert.That(terminalBinding!.Function.SupportsBackgrounding, Is.True);
            Assert.That(scope.TryGetBinding("manage_background_jobs", out var manageBinding, out _), Is.True);
            Assert.That(manageBinding!.Function.IsVisible, Is.False);
            Assert.That(manageBinding.Function.IsDefaultEnabled, Is.True);
            Assert.That(manageBinding.Function.SupportsParallelInvoke, Is.True);
        });

        // 4. Hidden manage_background_jobs (list / output / stop) works against RootChatContext.BackgroundJobs.
        scope.TryGetBinding("manage_background_jobs", out var jobToolBinding, out _);
        using var jobStopCts = new CancellationTokenSource();
        var ongoingJobTcs = new TaskCompletionSource<(ToolResultRecord Record, Exception? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callMsg = new FunctionCallChatMessage(LucideIconKind.Terminal, new DirectLocaleKey("terminal"));
        callMsg.DisplaySink.AppendBlock(new ChatPluginTextDisplayBlock("live line 1"));
        var job = historyRoot.BackgroundJobs.RegisterDetachedJob(
            historyRoot,
            historyData.SelectedTurns.LastOrDefault(),
            "execute_in_terminal",
            "echo live",
            callMsg,
            ongoingJobTcs.Task,
            jobStopCts,
            _ => { });

        var runServices = new ServiceCollection()
            .AddSingleton<ChatContext>(historyRoot)
            .BuildServiceProvider();
        var listText = await jobToolBinding!.Function.Function.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?> { ["action"] = "list" })
            {
                Services = runServices
            });
        var outputText = await jobToolBinding.Function.Function.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?> { ["action"] = "output", ["jobId"] = job.JobId })
            {
                Services = runServices
            });
        var stopText = await jobToolBinding.Function.Function.InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?> { ["action"] = "stop", ["jobId"] = job.JobId })
            {
                Services = runServices
            });

        Assert.Multiple(() =>
        {
            Assert.That(listText?.ToString(), Does.Contain(job.JobId).And.Contain("running"));
            Assert.That(outputText?.ToString(), Does.Contain("live line 1"));
            Assert.That(stopText?.ToString(), Does.Contain("Stop requested"));
            Assert.That(jobStopCts.IsCancellationRequested, Is.True);
        });
        ongoingJobTcs.TrySetCanceled(jobStopCts.Token);
    }

    [AvaloniaTest]
    public async Task ExplicitTurnModel_SteerAndBackgroundNotification_StayInSameTurnThroughStorageAndSyncGeneration()
    {
        using var harness = new TestChatHarness();
        using var plainData = new ChatData();
        var plainUser = plainData.StartTurn(new UserChatMessage("Plain question", []));
        var plainAssistant = new AssistantChatMessage();
        plainAssistant.AddSpan(new AssistantChatMessageTextSpan("Plain answer"));
        plainData.Append(plainUser, plainAssistant);

        var storage = new ChatTurnStorage(harness.DbFactory) { SyncAccountId = "acc-1" };
        await using (var db = await harness.DbFactory.CreateDbContextAsync())
        {
            await new ChatSyncStateStore(db).EnrollAsync("acc-1", 1);
        }
        await storage.SaveDataAsync(plainData);
        await storage.FreezeUploadAsync("acc-1", plainData.Metadata.Id, isStable: true);
        await using (var db = await harness.DbFactory.CreateDbContextAsync())
        {
            var plainSubmission = await db.SyncSubmissions.SingleAsync(row => row.ChatId == plainData.Metadata.Id);
            var plainHeader = MessagePackSerializer.Deserialize<ChatSyncUploadHeader>(plainSubmission.Header);
            Assert.That(plainHeader.RequiredDataGeneration, Is.EqualTo(1));
        }

        using var turnData = new ChatData();
        var turnStartNode = turnData.StartTurn(new UserChatMessage("Start turn", []));
        var asst1 = turnData.Append(turnStartNode, new AssistantChatMessage());
        var steerNode = turnData.Append(asst1, new UserChatMessage("Steer direction", []) { DeliveryMode = ChatInputDeliveryMode.Steer });
        var bgNode = turnData.Append(steerNode, new BackgroundJobNotificationChatMessage(
            "bg-1",
            "execute_in_terminal",
            "npm test",
            true,
            "All green",
            null,
            DateTimeOffset.UtcNow));
        var asst2 = turnData.Append(bgNode, new AssistantChatMessage());

        Assert.Multiple(() =>
        {
            Assert.That(turnData.SelectedTurns, Has.Count.EqualTo(1));
            Assert.That(turnStartNode.IsTurnStart, Is.True);
            Assert.That(steerNode.IsTurnStart, Is.False);
            Assert.That(bgNode.IsTurnStart, Is.False);
            Assert.That(steerNode.Turn, Is.SameAs(turnStartNode.Turn));
            Assert.That(bgNode.Turn, Is.SameAs(turnStartNode.Turn));
            Assert.That(asst2.Turn, Is.SameAs(turnStartNode.Turn));
        });

        await using (var root = new RootChatContext(turnData, null))
        {
            await Assert.ThatAsync(
                () => harness.ChatService.EditAsync(root, steerNode, new UserChatMessage("Cannot edit steer", [])),
                Throws.TypeOf<InvalidOperationException>());
        }

        await storage.SaveDataAsync(turnData);
        await storage.FreezeUploadAsync("acc-1", turnData.Metadata.Id, isStable: true);
        await using (var db = await harness.DbFactory.CreateDbContextAsync())
        {
            var steerSubmission = await db.SyncSubmissions.SingleAsync(row => row.ChatId == turnData.Metadata.Id);
            var steerHeader = MessagePackSerializer.Deserialize<ChatSyncUploadHeader>(steerSubmission.Header);
            Assert.That(steerHeader.RequiredDataGeneration, Is.EqualTo(3));

            var storedNodes = await db.Nodes
                .Where(row => row.ChatContextId == turnData.Metadata.Id)
                .ToDictionaryAsync(row => row.Id);
            Assert.That(storedNodes[turnStartNode.Id].TurnId, Is.EqualTo(turnStartNode.Id));
            Assert.That(storedNodes[steerNode.Id].TurnId, Is.EqualTo(turnStartNode.Id));
            Assert.That(storedNodes[bgNode.Id].TurnId, Is.EqualTo(turnStartNode.Id));
        }

        using var reloaded = await storage.GetChatContextAsync(turnData.Metadata);
        await using var reloadedRoot = new RootChatContext(reloaded, null);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.SelectedTurns, Has.Count.EqualTo(1));
            Assert.That(reloaded.SelectedTurns[0].UserNode?.Id, Is.EqualTo(turnStartNode.Id));
            Assert.That(reloaded.FindNode(turnStartNode.Id)?.IsTurnStart, Is.True);
            Assert.That(reloaded.FindNode(steerNode.Id)?.IsTurnStart, Is.False);
            Assert.That(reloaded.FindNode(bgNode.Id)?.IsTurnStart, Is.False);
        });
    }

    [AvaloniaTest]
    public async Task BackgroundJob_HoldsTurnOpen_QueuesWait_AndCompletedTurnFoldsIntoSingleSummaryAndFooter()
    {
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolRelease = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var plugin = new SteerTestPlugin(onBackgroundTool: async ct =>
        {
            toolStarted.TrySetResult();
            return await toolRelease.Task.WaitAsync(ct);
        });
        using var harness = new TestChatHarness(extraPlugin: plugin);
        var presentation = harness.Root.Presentation;
        await presentation.PrepareInitialWindowAsync();

        var requestIndex = 0;
        harness.ConfigureHandler(async (_, body, ct) =>
        {
            var idx = Interlocked.Increment(ref requestIndex);
            var contentsText = body.TryGetProperty("input", out var inputEl) ? inputEl.GetRawText() : body.GetRawText();
            if (idx == 1)
            {
                return CreateToolBatchSseResponse(("c-bg", "bg_probe", """{"title":"long task"}"""));
            }

            if (contentsText.Contains("Turn 2 queued", StringComparison.Ordinal))
            {
                turn2Started.TrySetResult();
                return CreateTextSseResponseWithUsage("Turn 2 reply", inputTokens: 10, outputTokens: 5);
            }

            if (contentsText.Contains("bg done", StringComparison.Ordinal))
            {
                return CreateTextSseResponseWithUsage("Final answer after steer and background task", inputTokens: 100, outputTokens: 40);
            }

            return CreateTextSseResponseWithUsage("Steer acknowledged, waiting for task.", inputTokens: 50, outputTokens: 20);
        });

        Assert.That(
            await harness.ChatService.SendMessageAsync(harness.Root, new UserChatMessage("Turn 1 start", []), harness.Assistant),
            Is.True);

        await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Steer mid-turn (triggers immediate foreground cancel -> move active tool to background -> steers in same turn).
        Assert.That(
            await harness.ChatService.SubmitMessageAsync(harness.Root, new UserChatMessage("Steer hint", []), harness.Assistant, ChatInputDeliveryMode.Steer),
            Is.True);

        // Wait until foreground pauses while background job holds Turn 1 open.
        await WaitUntilAsync(() => !harness.Root.IsBusy && harness.Root.HasActiveBackgroundJobsForCurrentTurn, TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(harness.Root.IsTurnActive, Is.True);
            Assert.That(harness.Root.Data.SelectedTurns, Has.Count.EqualTo(1));
            var waitingRow = presentation.Rows.OfType<BackgroundJobWaitingPresentationRow>().Single();
            Assert.That(waitingRow.ActiveCount, Is.EqualTo(1));
            Assert.That(waitingRow.IsRunning, Is.True);
        });

        // Retrying while only a detached background job holds the turn open must be ignored.
        var pausedAssistant = harness.Root.Data.SelectedTurns[0].SelectedNodes.Last(n => n.Message is AssistantChatMessage);
        harness.ChatService.Retry(harness.Root, pausedAssistant);
        Assert.That(harness.Root.Data.SelectedTurns[0].UserNode.ChoiceCount, Is.EqualTo(1));

        // Submit a Queue item while Turn 1 is waiting on background job -> must wait until Turn 1 finishes!
        Assert.That(
            await harness.ChatService.SubmitMessageAsync(harness.Root, new UserChatMessage("Turn 2 queued", []), harness.Assistant, ChatInputDeliveryMode.Queue),
            Is.True);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(turn2Started.Task.IsCompleted, Is.False);
            Assert.That(harness.Root.PendingInputs.WaitingItems, Has.Count.EqualTo(1));
            Assert.That(harness.Root.Data.SelectedTurns, Has.Count.EqualTo(1));
        });

        // Release background job -> Turn 1 wakes, finishes, THEN Queue launches Turn 2.
        toolRelease.TrySetResult("bg done");
        await turn2Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !harness.Root.IsTurnActive && harness.Root.Data.SelectedTurns.Count == 2, TimeSpan.FromSeconds(5));
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        var turn1 = harness.Root.Data.SelectedTurns[0];
        var turn2 = harness.Root.Data.SelectedTurns[1];
        var allRows = presentation.Rows.ToArray();
        var turn1Start = Array.FindIndex(allRows, r => r is ChatMessagePresentationRow msg && ReferenceEquals(msg.Node, turn1.UserNode));
        var turn2Start = Array.FindIndex(allRows, r => r is ChatMessagePresentationRow msg && ReferenceEquals(msg.Node, turn2.UserNode));
        Assert.That(turn1Start, Is.GreaterThanOrEqualTo(0));
        Assert.That(turn2Start, Is.GreaterThan(turn1Start));
        var turn1Rows = allRows[turn1Start..turn2Start];
        Assert.Multiple(() =>
        {
            Assert.That(presentation.Rows.OfType<BackgroundJobWaitingPresentationRow>(), Is.Empty);
            Assert.That(turn1.SelectedNodes.Count(n => !n.IsTurnStart && n.Message is UserChatMessage), Is.EqualTo(1));
            Assert.That(turn1.SelectedNodes.Count(n => n.Message is BackgroundJobNotificationChatMessage), Is.EqualTo(1));

            var summaries = turn1Rows.OfType<ProcessSummaryPresentationRow>().ToArray();
            Assert.That(summaries, Has.Length.EqualTo(1));

            var firstAssistantNode = turn1.SelectedNodes.First(n => n.Message is AssistantChatMessage);
            var lastAssistantNode = turn1.SelectedNodes.Last(n => n.Message is AssistantChatMessage);
            var footers = turn1Rows.OfType<TurnFooterPresentationRow>().ToArray();
            Assert.That(footers, Has.Length.EqualTo(1));
            Assert.That(footers[0].Branch.Node, Is.SameAs(firstAssistantNode));
            Assert.That(footers[0].AssistantNode, Is.SameAs(lastAssistantNode));
            // 50 + 100 input tokens, 20 + 40 output tokens (first tool call SSE had no usage).
            Assert.That(footers[0].State.UsageDetails.InputTokenCount, Is.EqualTo(150));
            Assert.That(footers[0].State.UsageDetails.OutputTokenCount, Is.EqualTo(60));
            Assert.That(turn1Rows.OfType<AssistantTextOutputPresentationRow>().Single().Markdown, Is.Not.Null);

            summaries[0].IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.That(
                presentation.Rows.OfType<ChatMessagePresentationRow>().Count(r => !r.Node.IsTurnStart && r.Node.Message is UserChatMessage),
                Is.EqualTo(1));
            var backgroundJobRows = presentation.Rows.OfType<ActivityGroupPresentationRow>()
                .SelectMany(g => g.Items)
                .Concat(presentation.Rows.OfType<ActivityItemPresentationRow>())
                .OfType<BackgroundJobActivityItemPresentationRow>()
                .ToArray();
            Assert.That(backgroundJobRows, Has.Length.EqualTo(1));
        });
    }

    [AvaloniaTest]
    public async Task ReadFileToolInvocation_ProducesSingleFileReferenceDisplayBlock()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"everywhere-read-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempPath, "hello");
        try
        {
            using var harness = new TestChatHarness(fileHandlers: [new TextFileHandler()]);
            var requestIndex = 0;
            harness.ConfigureHandler((_, _, _) =>
            {
                var idx = Interlocked.Increment(ref requestIndex);
                return Task.FromResult(idx == 1 ?
                    CreateToolBatchSseResponse(("call-read", "read_file", JsonSerializer.Serialize(new { path = tempPath }))) :
                    CreateTextSseResponseWithUsage("Read finished.", inputTokens: 10, outputTokens: 5));
            });

            Assert.That(
                await harness.ChatService.SendMessageAsync(harness.Root, new UserChatMessage("Read the file", []), harness.Assistant),
                Is.True);
            await WaitUntilAsync(() => !harness.Root.IsTurnActive, TimeSpan.FromSeconds(5));

            var toolMessage = harness.Root.Data.SelectedNodes
                .Select(n => n.Message)
                .OfType<AssistantChatMessage>()
                .SelectMany(a => a.Spans)
                .OfType<AssistantChatMessageFunctionCallSpan>()
                .SelectMany(s => s.Items)
                .Single();
            var blocks = toolMessage.DisplaySink.Items.OfType<ChatPluginFileReferencesDisplayBlock>().ToArray();
            Assert.That(blocks, Has.Length.EqualTo(1));
            Assert.That(blocks[0].References, Has.Count.EqualTo(1));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [AvaloniaTest]
    public async Task Retry_AfterSteerAndBackgroundWake_BranchesAtTurnResponseRoot()
    {
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolRelease = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var plugin = new SteerTestPlugin(onBackgroundTool: async ct =>
        {
            toolStarted.TrySetResult();
            return await toolRelease.Task.WaitAsync(ct);
        });
        using var harness = new TestChatHarness(extraPlugin: plugin);
        var presentation = harness.Root.Presentation;
        await presentation.PrepareInitialWindowAsync();

        var requestIndex = 0;
        harness.ConfigureHandler((_, body, _) =>
        {
            var idx = Interlocked.Increment(ref requestIndex);
            var contentsText = body.TryGetProperty("input", out var inputEl) ? inputEl.GetRawText() : body.GetRawText();
            if (idx == 1)
            {
                return Task.FromResult(CreateToolBatchSseResponse(("c-bg", "bg_probe", """{"title":"long task"}""")));
            }

            if (contentsText.Contains("bg done", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateTextSseResponse("Final answer after steer and background task"));
            }

            if (idx >= 4)
            {
                return Task.FromResult(CreateTextSseResponse("Retried clean turn answer"));
            }

            return Task.FromResult(CreateTextSseResponse("Steer acknowledged, waiting for task."));
        });

        Assert.That(
            await harness.ChatService.SendMessageAsync(harness.Root, new UserChatMessage("Turn 1 start", []), harness.Assistant),
            Is.True);

        await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(
            await harness.ChatService.SubmitMessageAsync(harness.Root, new UserChatMessage("Steer hint", []), harness.Assistant, ChatInputDeliveryMode.Steer),
            Is.True);

        await WaitUntilAsync(() => !harness.Root.IsBusy && harness.Root.HasActiveBackgroundJobsForCurrentTurn, TimeSpan.FromSeconds(5));
        toolRelease.TrySetResult("bg done");
        await WaitUntilAsync(() => !harness.Root.IsTurnActive, TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();

        var turn1 = harness.Root.Data.SelectedTurns.Single();
        var originalFirstAssistant = turn1.SelectedNodes.First(n => n.Message is AssistantChatMessage);
        var originalLastAssistant = turn1.SelectedNodes.Last(n => n.Message is AssistantChatMessage);
        Assert.That(originalFirstAssistant, Is.Not.SameAs(originalLastAssistant));

        harness.ChatService.Retry(harness.Root, originalLastAssistant);
        await WaitUntilAsync(() => !harness.Root.IsTurnActive, TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();

        var retriedTurn = harness.Root.Data.SelectedTurns.Single();
        var retriedAssistant = retriedTurn.SelectedNodes.First(n => n.Message is AssistantChatMessage);
        var footer = presentation.Rows.OfType<TurnFooterPresentationRow>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(retriedTurn.UserNode, Is.SameAs(turn1.UserNode));
            Assert.That(retriedTurn.UserNode.ChoiceCount, Is.EqualTo(2));
            Assert.That(retriedAssistant, Is.Not.SameAs(originalFirstAssistant));
            Assert.That(originalFirstAssistant.ChoiceCount, Is.EqualTo(1));
            Assert.That(footer.Branch.Node, Is.SameAs(retriedAssistant));
            Assert.That(footer.Branch.ChoiceCount, Is.EqualTo(2));
            Assert.That(footer.Branch.ChoiceIndex, Is.EqualTo(1));
        });

        footer.Branch.ChoiceIndex = 0;
        await WaitUntilAsync(
            () => ReferenceEquals(
                presentation.Rows.OfType<TurnFooterPresentationRow>().FirstOrDefault()?.Branch.Node,
                originalFirstAssistant),
            TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            var restoredTurn = harness.Root.Data.SelectedTurns.Single();
            Assert.That(restoredTurn.SelectedNodes.Count(n => !n.IsTurnStart && n.Message is UserChatMessage), Is.EqualTo(1));
            Assert.That(restoredTurn.SelectedNodes.Count(n => n.Message is BackgroundJobNotificationChatMessage), Is.EqualTo(1));
            var restoredFooter = presentation.Rows.OfType<TurnFooterPresentationRow>().Single();
            Assert.That(restoredFooter.Branch.Node, Is.SameAs(originalFirstAssistant));
            Assert.That(restoredFooter.AssistantNode, Is.SameAs(originalLastAssistant));
            Assert.That(restoredFooter.Branch.ChoiceIndex, Is.EqualTo(0));
        });

        presentation.Rows.OfType<TurnFooterPresentationRow>().Single().Branch.ChoiceIndex = 1;
        await WaitUntilAsync(
            () => ReferenceEquals(
                presentation.Rows.OfType<TurnFooterPresentationRow>().FirstOrDefault()?.Branch.Node,
                retriedAssistant),
            TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            var switchedBackFooter = presentation.Rows.OfType<TurnFooterPresentationRow>().Single();
            Assert.That(switchedBackFooter.Branch.Node, Is.SameAs(retriedAssistant));
            Assert.That(switchedBackFooter.Branch.ChoiceIndex, Is.EqualTo(1));
        });
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (predicate()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for condition.");
    }

    private static HttpResponseMessage CreateTextSseResponseWithUsage(string text, long inputTokens, long outputTokens)
    {
        var frames = new[]
        {
            JsonSerializer.Serialize(new
            {
                event_type = "step.start",
                index = 0,
                step = new
                {
                    type = "model_output",
                    content = new[] { new { type = "text", text } }
                }
            }),
            """{"event_type":"step.stop","index":0}""",
            JsonSerializer.Serialize(new
            {
                event_type = "interaction.completed",
                interaction = new
                {
                    id = "r1",
                    status = "completed",
                    usage = new
                    {
                        total_input_tokens = inputTokens,
                        total_output_tokens = outputTokens,
                        total_tokens = inputTokens + outputTokens
                    }
                }
            })
        };
        return BuildSse(frames);
    }

    private sealed class SteerTestPlugin : BuiltInChatPlugin
    {
        public override IDynamicLocaleKey HeaderKey => new DirectLocaleKey("SteerTest");
        public override IDynamicLocaleKey DescriptionKey => new DirectLocaleKey("SteerTest");
        public override LucideIconKind? Icon => LucideIconKind.Terminal;
        public override bool IsDefaultEnabled => true;

        private readonly Func<CancellationToken, Task<string>>? _onBackgroundTool;
        private readonly Func<string>? _onSerialTool;
        private readonly Func<IChatPluginUserInterface, CancellationToken, Task<string>>? _onConsentTool;

        public SteerTestPlugin(
            Func<CancellationToken, Task<string>>? onBackgroundTool = null,
            Func<string>? onSerialTool = null,
            Func<IChatPluginUserInterface, CancellationToken, Task<string>>? onConsentTool = null) : base("steer_test")
        {
            _onBackgroundTool = onBackgroundTool;
            _onSerialTool = onSerialTool;
            _onConsentTool = onConsentTool;

            _functionsSource.Add(new NativeChatFunction(
                RunBackgroundProbeAsync,
                ChatFunctionPermissions.None,
                supportsBackgrounding: true));
            _functionsSource.Add(new NativeChatFunction(
                RunSerialProbe,
                ChatFunctionPermissions.None,
                supportsParallelInvoke: false));
            _functionsSource.Add(new NativeChatFunction(
                RunConsentProbeAsync,
                ChatFunctionPermissions.None,
                canBypassApproval: false,
                supportsBackgrounding: true));
        }

        [ChatFunction("bg_probe")]
        private Task<string> RunBackgroundProbeAsync(
            [System.ComponentModel.Description("Title")] string title,
            CancellationToken cancellationToken) =>
            _onBackgroundTool is null ? Task.FromResult(title) : _onBackgroundTool(cancellationToken);

        [ChatFunction("serial_probe")]
        private string RunSerialProbe() => _onSerialTool?.Invoke() ?? "serial";

        [ChatFunction("consent_probe")]
        private Task<string> RunConsentProbeAsync(
            [FromChatServices] IChatPluginUserInterface ui,
            CancellationToken cancellationToken) =>
            _onConsentTool is null ? Task.FromResult("ok") : _onConsentTool(ui, cancellationToken);
    }

    private sealed class TestChatHarness : IDisposable
    {
        private readonly ChatTestDatabase _db;
        private readonly CompositeDisposable _disposables = new();
        private readonly RecordingHttpHandler _handler = new();
        private readonly RpcConnection _serverConnection;
        private readonly RpcConnection _clientConnection;
        private readonly AutomationHostSession _automationSession;

        public Settings Settings { get; }
        public IDbContextFactory<ChatV2DbContext> DbFactory => _db.Factory;
        public ChatPluginManager Plugins { get; }
        public ChatContextManager ContextManager { get; }
        public ChatService ChatService { get; }
        public CustomAssistant Assistant { get; }
        public RootChatContext Root => ContextManager.Current;
        public IReadOnlyList<JsonElement> Requests => _handler.Requests;
        public List<string> Logs { get; } = [];

        public TestChatHarness(
            BuiltInChatPlugin? extraPlugin = null,
            ToolApprovalMode approvalMode = ToolApprovalMode.FullAccess,
            IReadOnlyList<FileHandler>? fileHandlers = null)
        {
            _db = ChatTestDatabase.Create();
            var storage = new ChatTurnStorage(_db.Factory);
            var blobs = new BlobStorage(_db.Factory);
            var services = Substitute.For<IServiceProvider>();
            Settings = new Settings(services);
            services.GetService(typeof(Settings)).Returns(Settings);
            Settings.Plugin.ApprovalMode = approvalMode;
            Settings.ChatWindow.AutomaticallyGenerateTitle = false;

            var pipeName = $"evqs.{Guid.NewGuid():N}"[..13];
            var serverStream = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var waitConnect = serverStream.WaitForConnectionAsync();
            var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            clientStream.Connect(5000);
            waitConnect.GetAwaiter().GetResult();

            var rpcOptions = new RpcConnectionOptions { RequireHandshake = false };
            _serverConnection = new RpcConnection(serverStream, isServer: true, rpcOptions);
            _clientConnection = new RpcConnection(clientStream, isServer: false, rpcOptions);
            _automationSession = new AutomationHostSession(
                Substitute.For<IVisualElementBackend>(),
                Substitute.For<IVisualPickerResolver>(),
                Substitute.For<ITextSelectionMonitorFactory>());
            _automationSession.Bind(_serverConnection);
            _automationSession.OnAuthenticated(
                new RpcHandshake
                {
                    AssemblyInformationalVersion = "test",
                    Role = ProcessRoleNames.ToWireName(ProcessRole.Main),
                    ProcessId = Environment.ProcessId,
                    DesktopSessionId = "test",
                });
            _serverConnection.Start();
            _clientConnection.Start();

            var filePlugin = new FileSystemPlugin(
                Settings,
                new FileHandlerContextFactory(fileHandlers ?? []),
                NullLogger<FileSystemPlugin>.Instance);
            var pluginsList = new List<BuiltInChatPlugin> { filePlugin };
            if (extraPlugin is not null) pluginsList.Add(extraPlugin);

            Plugins = new ChatPluginManager(
                services,
                pluginsList,
                Settings,
                Substitute.For<IRuntimeManager>(),
                NullLogger<ChatPluginManager>.Instance);
            _disposables.Add(Disposable.Create(Plugins.Dispose));

            ContextManager = new ChatContextManager(Settings, storage, NullLogger<ChatContextManager>.Instance);

            var promptService = Substitute.For<IPromptService>();
            promptService.DefaultPrompt.Returns(new PromptDefinition(Guid.Empty, null, "Test system prompt.", IsBuiltIn: true));
            promptService.GetPromptAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<PromptDefinition?>(null));

            var skillProvider = Substitute.For<ISkillPromptProvider>();
            skillProvider.GetPrompt(Arg.Any<ToolCallStatus>()).Returns(string.Empty);

            var httpFactory = Substitute.For<IHttpClientFactory>();
            httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_handler, false));

            var modelFactory = new ModelSessionFactory(httpFactory, NullLoggerFactory.Instance);
            var specializations = new AssistantSpecializationResolver(
                new AssistantCatalog(Substitute.For<IPresetModelProvider>(), Substitute.For<IOfficialModelProvider>()));
            var visual = new ChatVisualService(new InMemoryHostConnectionSource(_clientConnection));
            _disposables.Add(visual);

            ChatService = new ChatService(
                ContextManager,
                Plugins,
                modelFactory,
                specializations,
                blobs,
                Settings,
                promptService,
                skillProvider,
                Substitute.For<IStatisticsRecorder>(),
                visual,
                Substitute.For<IVisualContextScanEffect>(),
                services,
                new CapturingLogger<ChatService>(Logs));

            Assistant = new CustomAssistant
            {
                Configuration = new AdvancedAssistantConfiguration
                {
                    Schema = ModelProviderSchema.GoogleInteractions,
                    Endpoint = "https://fixture.invalid/v1",
                    ModelId = "gemini-test",
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text
                }
            };
            Settings.Model.CustomAssistants.Add(Assistant);
            Settings.Model.SelectedCustomAssistant = Assistant;
        }

        public void ConfigureHandler(Func<int, JsonElement, CancellationToken, Task<HttpResponseMessage>> handler) =>
            _handler.Configure(handler);

        public void Dispose()
        {
            ContextManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _disposables.Dispose();
            _clientConnection.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _serverConnection.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _automationSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _handler.Dispose();
            _db.Dispose();
        }
    }

    private sealed class InMemoryHostConnectionSource(RpcConnection connection) : IHostConnectionSource
    {
        public ValueTask<RpcConnection> GetConnectionAsync(ProcessRole role, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(connection);

        public async IAsyncEnumerable<RpcConnection> WatchConnectionsAsync(
            ProcessRole role,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return connection;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private static async Task<ChatPluginUserInterfaceConsentRequestItem> WaitForConsentItemAsync(RootChatContext root, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            var item = root.UserInterfaceBroker.ChatPluginUserInterfaceItems
                .OfType<ChatPluginUserInterfaceConsentRequestItem>()
                .FirstOrDefault();
            if (item is not null)
            {
                return item;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for consent request item.");
    }

    private sealed class CapturingLogger<T>(List<string> logs) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            logs.Add($"[{logLevel}] {formatter(state, exception)} {exception}");
        }
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private Func<int, JsonElement, CancellationToken, Task<HttpResponseMessage>>? _handler;

        public List<JsonElement> Requests { get; } = [];

        public void Configure(Func<int, JsonElement, CancellationToken, Task<HttpResponseMessage>> handler) =>
            _handler = handler;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var clone = doc.RootElement.Clone();
            int count;
            lock (Requests)
            {
                Requests.Add(clone);
                count = Requests.Count;
            }

            if (_handler is null) return CreateTextSseResponse("ok");
            return await _handler(count, clone, cancellationToken);
        }
    }

    private static HttpResponseMessage CreateTextSseResponse(string text)
    {
        var frames = new[]
        {
            JsonSerializer.Serialize(new
            {
                event_type = "step.start",
                index = 0,
                step = new
                {
                    type = "model_output",
                    content = new[] { new { type = "text", text } }
                }
            }),
            """{"event_type":"step.stop","index":0}""",
            """{"event_type":"interaction.completed","interaction":{"id":"r1","status":"completed"}}"""
        };
        return BuildSse(frames);
    }

    private static HttpResponseMessage CreateToolBatchSseResponse(params (string Id, string Name, string ArgsJson)[] calls)
    {
        var frames = new List<string>(calls.Length * 2 + 1);
        for (var i = 0; i < calls.Length; i++)
        {
            var (id, name, argsJson) = calls[i];
            using var argsDoc = JsonDocument.Parse(argsJson);
            frames.Add(
                JsonSerializer.Serialize(new
                {
                    event_type = "step.start",
                    index = i,
                    step = new
                    {
                        type = "function_call",
                        id,
                        name,
                        arguments = argsDoc.RootElement
                    }
                }));
            frames.Add(JsonSerializer.Serialize(new { event_type = "step.stop", index = i }));
        }

        frames.Add("""{"event_type":"interaction.completed","interaction":{"id":"rt","status":"requires_action"}}""");
        return BuildSse(frames);
    }

    private static HttpResponseMessage CreateApprovalDecisionSseResponse(string decision) =>
        CreateToolBatchSseResponse(
            ("call-approve", "submit_approval", JsonSerializer.Serialize(new { decision, reason = "ok" })));

    private static HttpResponseMessage BuildSse(IEnumerable<string> frames) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(frames.Select(static f => $"data: {f}\n\n")), Encoding.UTF8, "text/event-stream")
        };
}
