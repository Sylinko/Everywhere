using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Automation;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Harness;
using Everywhere.Messages;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.Prompting.Documents;
using Everywhere.Skills;
using Everywhere.Statistics;
using Everywhere.Storage;
using Everywhere.StrategyEngine;
using Everywhere.Utilities;
using Everywhere.Views;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MEAI = Microsoft.Extensions.AI;
using PromptTemplateRenderer = Everywhere.AI.Prompts.PromptTemplateRenderer;

namespace Everywhere.Chat;

public sealed partial class ChatService : IChatService
{
    private readonly IChatContextManager _chatContextManager;
    private readonly IChatPluginManager _chatPluginManager;
    private readonly IModelSessionFactory _modelSessionFactory;
    private readonly AssistantSpecializationResolver _assistantSpecializationResolver;
    private readonly IBlobStorage _blobStorage;
    private readonly Settings _settings;
    private readonly IPromptService _promptService;
    private readonly ISkillPromptProvider _skillPromptProvider;
    private readonly IStatisticsRecorder _statisticsRecorder;
    private readonly ChatVisualService _visualService;
    private readonly IVisualContextScanEffect _visualContextScanEffect;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ChatService> _logger;
    private readonly AsyncLocal<Guid?> _currentTurnEventId = new();
    private readonly AsyncLocal<Guid?> _currentModelInvocationEventId = new();

    private readonly ActivitySource _activitySource = new(typeof(ChatService).FullName.NotNull(), App.Version);
    private readonly Meter _meter = new(typeof(ChatService).FullName.NotNull(), App.Version);
    private readonly Counter<int> _chatRequestsCounter;
    private readonly Counter<int> _chatTopicsCounter;
    private readonly Histogram<double> _timeToFirstTokenHistogram;
    private readonly Histogram<long> _inputTokensHistogram;
    private readonly Histogram<long> _cachedInputTokensHistogram;
    private readonly Histogram<long> _outputTokensHistogram;
    private readonly Histogram<long> _reasoningTokensHistogram;
    private readonly Counter<long> _toolCallsCounter;

    public ChatService(
        IChatContextManager chatContextManager,
        IChatPluginManager chatPluginManager,
        IModelSessionFactory modelSessionFactory,
        AssistantSpecializationResolver assistantSpecializationResolver,
        IBlobStorage blobStorage,
        Settings settings,
        IPromptService promptService,
        ISkillPromptProvider skillPromptProvider,
        IStatisticsRecorder statisticsRecorder,
        ChatVisualService visualService,
        IVisualContextScanEffect visualContextScanEffect,
        IServiceProvider serviceProvider,
        ILogger<ChatService> logger)
    {
        _chatContextManager = chatContextManager;
        _chatPluginManager = chatPluginManager;
        _modelSessionFactory = modelSessionFactory;
        _assistantSpecializationResolver = assistantSpecializationResolver;
        _blobStorage = blobStorage;
        _settings = settings;
        _promptService = promptService;
        _skillPromptProvider = skillPromptProvider;
        _statisticsRecorder = statisticsRecorder;
        _visualService = visualService;
        _visualContextScanEffect = visualContextScanEffect;
        _serviceProvider = serviceProvider;
        _logger = logger;

        _chatRequestsCounter = _meter.CreateCounter<int>("gen_ai.chat.requests");
        _chatTopicsCounter = _meter.CreateCounter<int>("gen_ai.chat.topics");
        _timeToFirstTokenHistogram = _meter.CreateHistogram<double>("gen_ai.request.ttft", "s");
        _inputTokensHistogram = _meter.CreateHistogram<long>("gen_ai.usage.input_tokens", "token");
        _cachedInputTokensHistogram = _meter.CreateHistogram<long>("gen_ai.usage.cached_input_tokens", "token");
        _outputTokensHistogram = _meter.CreateHistogram<long>("gen_ai.usage.output_tokens", "token");
        _reasoningTokensHistogram = _meter.CreateHistogram<long>("gen_ai.usage.reasoning_tokens", "token");
        _toolCallsCounter = _meter.CreateCounter<long>("gen_ai.tool.calls");
    }

    /// <inheritdoc />
    public Task<bool> SendMessageAsync(ChatContext chatContext, UserChatMessage message, CustomAssistant? customAssistant)
    {
        var chatData = chatContext.Data;
        return chatContext.TryStartAsync(
            () =>
            {
                // Publish the first node and initial title before consuming the composer's input.
                // Visual processing, statistics and generation remain outside this short operation.
                var shouldCreateTitle = !chatData.HasHistory && string.IsNullOrWhiteSpace(chatData.Metadata.Topic);
                var titleInput = shouldCreateTitle ? GetTitleInput(message) : null;
                var node = chatData.Add(message);
                if (titleInput is not null)
                {
                    chatData.Metadata.Rename(string.Join(" ", titleInput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
                }

                return (UserNode: node, TitleInput: titleInput, Summary: chatData.Metadata.Topic);
            },
            async (initialized, cancellationToken) =>
            {
                var (userNode, titleInput, summary) = initialized;
                using var activity = _activitySource.StartActivity();
                activity?.SetTag("chat.context.id", chatData.Metadata.Id);
                try
                {
                    if (titleInput is { Length: > 0 } && customAssistant is not null && _settings.ChatWindow.AutomaticallyGenerateTitle)
                    {
                        chatContext
                            .RunBackgroundAsync(
                                token => GenerateTopicAsync(customAssistant, titleInput, chatContext, summary, token),
                                CancellationToken.None)
                            .Detach(_logger.ToExceptionHandler());
                    }

                    await _visualService.AdvanceTurnAsync(chatContext.VisualState, cancellationToken);
                    var turnEventId = await _statisticsRecorder.RecordTurnAsync(
                        chatData,
                        userNode,
                        StatisticsTurnKind.Send,
                        cancellationToken);
                    using var turnScope = BeginStatisticsTurn(turnEventId);

                    if (customAssistant is null)
                    {
                        await chatContext.DispatchAsync(() => chatData.Append(userNode, CreateCustomAssistantNotSelectedErrorAssistantChatMessage()));
                        return;
                    }

                    var appendTailNode = await ProcessUserChatMessageAsync(chatContext, customAssistant, userNode, cancellationToken);

                    var assistantChatMessage = new AssistantChatMessage { IsBusy = true };
                    var assistantNode = await chatContext.DispatchAsync(() => chatData.Append(appendTailNode, assistantChatMessage));

                    var systemPromptOverride = message.As<UserStrategyChatMessage>()?.Strategy.SystemPrompt;
                    await GenerateAsync(
                        chatContext,
                        customAssistant,
                        assistantNode,
                        CompressionBoundary.Before(userNode),
                        systemPromptOverride: systemPromptOverride,
                        cancellationToken: cancellationToken);
                }
                finally
                {
                    DisposeVisualAnchors(message);
                }
            },
            _logger.ToExceptionHandler());
    }

    /// <inheritdoc />
    public Task<bool> EditAsync(ChatContext chatContext, ChatMessageNode oldNode, UserChatMessage newMessage)
    {
        if (oldNode.Message.Role != MEAI.ChatRole.User)
        {
            throw new InvalidOperationException("Only user messages can be edited.");
        }

        var chatData = oldNode.Data;
        var customAssistant = _settings.Model.SelectedCustomAssistant;
        return chatContext.TryStartAsync(
            () => chatData.CreateBranchOn(oldNode, newMessage),
            async (userNode, cancellationToken) =>
            {
                using var activity = _activitySource.StartActivity();
                activity?.SetTag("chat.context.id", chatData.Metadata.Id);

                try
                {
                    await _visualService.AdvanceTurnAsync(chatContext.VisualState, cancellationToken);
                    var turnEventId = await _statisticsRecorder.RecordTurnAsync(
                        chatData,
                        userNode,
                        StatisticsTurnKind.Edit,
                        cancellationToken);
                    using var turnScope = BeginStatisticsTurn(turnEventId);

                    if (customAssistant is null)
                    {
                        await chatContext.DispatchAsync(() => chatData.Append(userNode, CreateCustomAssistantNotSelectedErrorAssistantChatMessage()));
                        return;
                    }

                    var appendTailNode = await ProcessUserChatMessageAsync(chatContext, customAssistant, userNode, cancellationToken);

                    var assistantChatMessage = new AssistantChatMessage { IsBusy = true };
                    var assistantNode = await chatContext.DispatchAsync(() => chatData.Append(appendTailNode, assistantChatMessage));

                    var systemPromptOverride = newMessage.As<UserStrategyChatMessage>()?.Strategy.SystemPrompt;
                    await GenerateAsync(
                        chatContext,
                        customAssistant,
                        assistantNode,
                        CompressionBoundary.Before(userNode),
                        systemPromptOverride: systemPromptOverride,
                        cancellationToken: cancellationToken);
                }
                finally
                {
                    DisposeVisualAnchors(newMessage);
                }
            },
            _logger.ToExceptionHandler());
    }

    public void Retry(ChatContext chatContext, ChatMessageNode node)
    {
        if (node.Message.Role != MEAI.ChatRole.Assistant)
        {
            throw new InvalidOperationException("Only assistant messages can be retried.");
        }

        var chatData = node.Data;
        var customAssistant = _settings.Model.SelectedCustomAssistant;

        chatContext.TryExecute(
            async cancellationToken =>
            {
                using var activity = _activitySource.StartActivity();
                activity?.SetTag("chat.context.id", chatData.Metadata.Id);

                await _visualService.AdvanceTurnAsync(chatContext.VisualState, cancellationToken);
                if (customAssistant is null)
                {
                    await chatContext.DispatchAsync(() => chatData.CreateBranchOn(node, CreateCustomAssistantNotSelectedErrorAssistantChatMessage()));
                    return;
                }

                var assistantChatMessage = new AssistantChatMessage { IsBusy = true };
                var userNode = node.Turn?.UserNode;
                var assistantNode = await chatContext.DispatchAsync(() => chatData.CreateBranchOn(node, assistantChatMessage));

                var turnEventId = await _statisticsRecorder.RecordTurnAsync(
                    chatData,
                    userNode,
                    StatisticsTurnKind.Retry,
                    cancellationToken);
                using var turnScope = BeginStatisticsTurn(turnEventId);

                await GenerateAsync(
                    chatContext,
                    customAssistant,
                    assistantNode,
                    CompressionBoundary.Before(userNode ?? assistantNode),
                    cancellationToken: cancellationToken);
            },
            _logger.ToExceptionHandler());
    }

    public void Continue(ChatContext chatContext, ChatMessageNode node)
    {
        if (node.Message.Role != MEAI.ChatRole.Assistant)
        {
            throw new InvalidOperationException("Only assistant messages can be continued.");
        }

        var chatData = node.Data;
        var customAssistant = _settings.Model.SelectedCustomAssistant;

        chatContext.TryExecute(
            async cancellationToken =>
            {
                using var activity = _activitySource.StartActivity();
                activity?.SetTag("chat.context.id", chatData.Metadata.Id);

                var appendTailNode = await chatContext.DispatchAsync(() =>
                {
                    var tail = chatData.GetContinuationTail(node);
                    chatData.RecordPathChoices(tail);
                    return tail;
                });

                if (customAssistant is null)
                {
                    await chatContext.DispatchAsync(() => chatData.Append(
                        appendTailNode,
                        CreateCustomAssistantNotSelectedErrorAssistantChatMessage()));
                    return;
                }

                var assistantChatMessage = new AssistantChatMessage { IsBusy = true };
                var assistantNode = await chatContext.DispatchAsync(() => chatData.Append(appendTailNode, assistantChatMessage));

                await GenerateAsync(
                    chatContext,
                    customAssistant,
                    assistantNode,
                    CompressionBoundary.Through(node),
                    purpose: StatisticsModelInvocationPurpose.ContinueResponse,
                    cancellationToken: cancellationToken);
            },
            _logger.ToExceptionHandler());
    }

    public void CompactContext()
    {
        var chatContext = _chatContextManager.Current;
        var chatData = chatContext.Data;
        var assistant = _settings.Model.SelectedCustomAssistant;

        chatContext.TryExecute(
            async cancellationToken =>
            {
                using var activity = _activitySource.StartActivity();
                activity?.SetTag("chat.context.id", chatData.Metadata.Id);

                var appendTailNode = await chatContext.DispatchAsync(() =>
                {
                    var tail = chatData.SelectedNodes[^1];
                    chatData.RecordPathChoices(tail);
                    return tail;
                });

                if (assistant is null)
                {
                    await chatContext.DispatchAsync(() => chatData.Append(
                        appendTailNode,
                        CreateCustomAssistantNotSelectedErrorAssistantChatMessage()));
                    return;
                }

                GenerationContext? generationContext = null;
                try
                {
                    generationContext = await CreateGenerationContextAsync(
                        chatContext,
                        appendTailNode,
                        assistant,
                        null,
                        cancellationToken,
                        isConversationTurn: false);
                    await chatContext.DispatchAsync(() => chatContext.GenerationContext = generationContext);
                    await EnsureVisualContextResetNoticeAsync(chatContext, generationContext, null, cancellationToken);
                    await CompactContextAsync(
                        chatContext,
                        generationContext,
                        ContextCompressionTrigger.Manual,
                        CompressionBoundary.Through(generationContext.AppendTailNode),
                        cancellationToken);
                }
                finally
                {
                    await chatContext.DispatchAsync(() =>
                    {
                        if (ReferenceEquals(chatContext.GenerationContext, generationContext))
                        {
                            chatContext.GenerationContext = null;
                        }
                    });
                    if (generationContext is not null)
                    {
                        await generationContext.DisposeAsync();
                    }
                }
            },
            _logger.ToExceptionHandler());
    }

    private static AssistantChatMessage CreateCustomAssistantNotSelectedErrorAssistantChatMessage() =>
        new()
        {
            ErrorMessageKey = new DynamicLocaleKey(LocaleKey.ChatService_Error_CustomAssistantNotSelected),
            FinishedAt = DateTimeOffset.UtcNow,
        };

    private async Task<ChatMessageNode> ProcessUserChatMessageAsync(
        ChatContext chatContext,
        CustomAssistant customAssistant,
        ChatMessageNode userNode,
        CancellationToken cancellationToken)
    {
        var chatData = chatContext.Data;
        var userChatMessage = (UserChatMessage)userNode.Message;
        var appendTailNode = userNode;
        using var activity = _activitySource.StartActivity();
        activity?.SetTag("chat.context.id", chatData.Metadata.Id);

        var visualElementAttachments = userChatMessage
            .Attachments
            .AsValueEnumerable()
            .OfType<VisualElementAttachment>()
            .ToArray();
        await RecordImageAttachmentStatisticsAsync(chatData, userChatMessage);
        if (visualElementAttachments.Length == 0) return appendTailNode;

        var analyzingContextMessage = new ActionChatMessage(
            LucideIconKind.TextSearch,
            LocaleKey.ActionChatMessage_Header_AnalyzingContext)
        {
            IsBusy = true
        };

        try
        {
            appendTailNode = await chatContext.DispatchAsync(() => chatData.Append(userNode, analyzingContextMessage));

            var approximateTokenLimit = customAssistant.VisualContextLengthLimit.ToTokenLimit();
            var validAttachments = new List<VisualElementAttachment>(visualElementAttachments.Length);
            var anchors = new List<RemoteVisualAnchor>(visualElementAttachments.Length);
            foreach (var attachment in visualElementAttachments)
            {
                if (!attachment.IsElementValid || attachment.Anchor is not { } anchor) continue;

                var destinationAnchor = await _visualService.MoveAnchorAsync(
                    chatContext.VisualState,
                    anchor,
                    cancellationToken: cancellationToken);
                if (!ReferenceEquals(anchor, destinationAnchor))
                {
                    await chatContext.DispatchAsync(() => attachment.ReplaceAnchor(anchor, destinationAnchor));
                }

                validAttachments.Add(attachment);
                anchors.Add(destinationAnchor);
            }

            if (anchors.Count == 0) return appendTailNode;

            using var scanScope = _visualContextScanEffect.Begin(cancellationToken);
            var (visualContextId, response) = await _visualService.BuildAnchorsAsync(
                chatContext.VisualState,
                anchors,
                new VisualQueryRequest(),
                targetTokenBudget: approximateTokenLimit,
                scanScope: scanScope,
                cancellationToken: cancellationToken);
            scanScope.Complete();
            await chatContext.DispatchAsync(() =>
            {
                validAttachments[0].Content = new PromptText(response.Content);
                for (var index = 1; index < validAttachments.Count; index++)
                {
                    validAttachments[index].Content = null;
                }

                if (response.RepresentedTargetCount > 0)
                {
                    userChatMessage.VisualContextId = visualContextId;
                }

                userNode.NotifyMessageChanged(isDurable: true);
            });

            await _statisticsRecorder.RecordVisualContextAsync(
                new StatisticsVisualContextDraft(
                    _currentTurnEventId.Value,
                    chatData.Metadata.Id,
                    StatisticsVisualContextSource.AutomaticAttachmentProcessing,
                    ElementCount: response.RepresentedTargetCount),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            ex = ChatExceptionNormalizer.Handle(ex, null);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message.Trim());
            await chatContext.DispatchAsync(() => analyzingContextMessage.ErrorMessageKey = ex.GetFriendlyMessage());
            _logger.LogError(ex, "Error analyzing visual tree");
        }
        finally
        {
            await chatContext.DispatchAsync(() =>
            {
                analyzingContextMessage.FinishedAt = DateTimeOffset.UtcNow;
                analyzingContextMessage.IsBusy = false;
            });
        }

        return appendTailNode;
    }

    private static void DisposeVisualAnchors(UserChatMessage message)
    {
        foreach (var attachment in message.Attachments.OfType<VisualElementAttachment>()) attachment.Dispose();
    }

    private async Task<(IServiceProvider Services, IChatPluginScope? Scope)> BuildRunServicesAsync(
        ModelSession session,
        ChatContext chatContext,
        ChatMessageNode appendTailNode,
        Assistant assistant,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity();

        var services = new ServiceCollection();
        services.AddSingleton<IChatService>(this);
        services.AddSingleton(session.ChatClient);
        services.AddSingleton(session);
        services.AddSingleton(_chatContextManager);
        services.AddSingleton(chatContext);
        services.AddSingleton(assistant);
        services.AddTransient<IChatPluginDisplaySink>(static x =>
            x.GetRequiredService<ChatContext>().GenerationContext?.FunctionCallContext.Value?.DisplaySink ??
            throw new InvalidOperationException($"No {nameof(IChatPluginDisplaySink)} is available in current function call context."));
        services.AddTransient<IChatPluginUserInterface>(static x =>
            x.GetRequiredService<ChatContext>().GenerationContext?.FunctionCallContext.Value ??
            throw new InvalidOperationException($"No {nameof(IChatPluginUserInterface)} is available in current function call context."));
        services.AddTransient<IStorageProvider>(_ =>
            _serviceProvider.GetService<ChatWindow>()?.StorageProvider ?? App.StorageProvider);

        IChatPluginScope? chatPluginScope = null;
        var customAssistant = assistant as CustomAssistant;
        if (session.Configuration.SupportsToolCall && (customAssistant?.IsToolCallEnabled ?? true))
        {
            var userMessage = appendTailNode.Turn?.UserNode.Message as UserChatMessage;
            var strategyToolRulesets = userMessage?.As<UserStrategyChatMessage>()?.Strategy.ToolPatternRulesets;
            var toolRulesets = new ToolRulesetsPipeline(
            [
                customAssistant?.ToolEnablementRulesets,
                strategyToolRulesets,
                chatContext.ToolPatternRulesets
            ]);

            chatPluginScope = await _chatPluginManager.CreateScopeAsync(
                session.Configuration,
                chatContext,
                appendTailNode,
                toolRulesets,
                cancellationToken);
            services.AddSingleton(chatPluginScope);
            activity?.SetTag("plugins.count", chatPluginScope.Plugins.Count);
        }

        return (services.BuildServiceProvider(), chatPluginScope);
    }

    private async Task<GenerationContext> CreateGenerationContextAsync(
        ChatContext chatContext,
        ChatMessageNode appendTailNode,
        Assistant assistant,
        string? systemPromptOverride,
        CancellationToken cancellationToken,
        bool isConversationTurn = true)
    {
        var approvalState = chatContext.InheritedApprovalState ?? new GenerationApprovalState(_settings.Plugin.ApprovalMode);
        var session = _modelSessionFactory.Create(assistant);
        IServiceProvider? runServices = null;
        try
        {
            if (appendTailNode.Turn is { } turn)
            {
                await chatContext.EnsureTurnLoadedAsync(turn, cancellationToken);
            }

            var (services, pluginScope) = await BuildRunServicesAsync(session, chatContext, appendTailNode, assistant, cancellationToken);
            runServices = services;
            cancellationToken.ThrowIfCancellationRequested();

            var customAssistant = assistant as CustomAssistant;
            var toolCallStatus = !session.Configuration.SupportsToolCall ? ToolCallStatus.NotSupported :
                customAssistant?.IsToolCallEnabled == false ? ToolCallStatus.Disabled : ToolCallStatus.Enabled;
            var promptRenderer = new ScopedPromptRenderer(
                SystemPromptPlaceholderSource.Shared,
                new PromptPlaceholderContext(
                    SkillsPromptResolver: () => _skillPromptProvider.GetPrompt(toolCallStatus),
                    WorkingDirectoryResolver: () => chatContext.WorkingDirectory));

            string systemPromptTemplate;
            if (systemPromptOverride is null)
            {
                var promptId = customAssistant?.SystemPromptId ?? Guid.Empty;
                var resolvedPrompt = await _promptService.GetPromptAsync(promptId, cancellationToken) ?? _promptService.DefaultPrompt;
                systemPromptTemplate = resolvedPrompt.Template;
            }
            else
            {
                systemPromptTemplate = systemPromptOverride;
            }

            return new GenerationContext(
                services,
                session,
                pluginScope,
                promptRenderer,
                promptRenderer.RenderSystemPrompt(systemPromptTemplate),
                session.Configuration.InputModalities,
                customAssistant is not null ?
                    ContextUsageSnapshot.NormalizeCompressionThresholdPercentage(customAssistant.ContextCompressionThreshold) :
                    ContextUsageSnapshot.DefaultCompressionThresholdPercentage,
                customAssistant?.MaxContextRounds ?? -1,
                approvalState,
                isConversationTurn,
                appendTailNode);
        }
        catch
        {
            if (runServices is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (runServices is IDisposable disposable)
            {
                disposable.Dispose();
            }

            session.Dispose();
            throw;
        }
    }

    public async Task<ChatGenerationResult> GenerateAsync(
        ChatContext chatContext,
        Assistant assistant,
        ChatMessageNode assistantNode,
        CompressionBoundary compressionBoundary,
        string? systemPromptOverride = null,
        bool enableNotifications = true,
        StatisticsModelInvocationPurpose purpose = StatisticsModelInvocationPurpose.ChatResponse,
        CancellationToken cancellationToken = default)
    {
        var chatData = chatContext.Data;
        var assistantChatMessage = (AssistantChatMessage)assistantNode.Message;
        Activity? activity = null;
        GenerationContext? generationContext = null;
        var previousModelInvocationEventId = _currentModelInvocationEventId.Value;
        try
        {
            generationContext = await CreateGenerationContextAsync(chatContext, assistantNode, assistant, systemPromptOverride, cancellationToken);
            await chatContext.DispatchAsync(() => chatContext.GenerationContext = generationContext);
            var session = generationContext.Session;
            activity = _activitySource.StartChatActivity("chat", session.Configuration);
            activity?.SetTag("id", chatData.Metadata.Id);
            var hasAttemptedAutomaticCompaction = false;
            var hasAttemptedContextLengthRecovery = false;

            await EnsureVisualContextResetNoticeAsync(chatContext, generationContext, assistantChatMessage, cancellationToken);

            if (assistantChatMessage.HasOutputContent) compressionBoundary = CompressionBoundary.Through(assistantNode);

            if (await ResolvePendingAutomaticCompressionTriggerAsync(chatContext, generationContext.ContextCompressionThreshold, cancellationToken) is
                { } compressionTrigger)
            {
                hasAttemptedAutomaticCompaction = true;
                var compacted = await CompactContextAsync(
                    chatContext,
                    generationContext,
                    compressionTrigger,
                    compressionBoundary,
                    cancellationToken);
                if (!compacted.HasAppliedSummary && compressionTrigger == ContextCompressionTrigger.ContextLengthRecovery)
                {
                    return new ChatGenerationResult(assistantChatMessage, compacted.Error);
                }
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await EnsureVisualContextResetNoticeAsync(chatContext, generationContext, assistantChatMessage, cancellationToken);

                var selection = await ChatHistoryBuilder.SelectContextMessagesAsync(
                    chatContext,
                    generationContext.GetPath(),
                    generationContext.MaxContextRounds,
                    session.Configuration.ContextLimit,
                    cancellationToken);

                ModelInvocationResult invocationResult;
                try
                {
                    invocationResult = await RequestWithImageRecoveryAsync(
                        chatContext,
                        generationContext,
                        selection.Messages,
                        async history =>
                        {
                            var currentTools = session.Configuration.SupportsToolCall &&
                                generationContext.PluginScope?.Declarations is { Count: > 0 } declarations
                                    ? declarations
                                    : null;
                            var resolvedPrefix = await chatContext.DispatchAsync(() => chatContext.PrefixState.Apply(
                                history,
                                currentTools,
                                new ContextPrefixIdentity(
                                    session.Dialect,
                                    session.Configuration.ModelId,
                                    session.Configuration.SupportsToolCall,
                                    selection.ActiveSummaryNodeId,
                                    selection.WindowStartNodeId),
                                DateTimeOffset.UtcNow));
                            return await GetStreamingChatMessageContentsAsync(
                                generationContext,
                                chatContext,
                                resolvedPrefix.History,
                                resolvedPrefix.Tools,
                                assistantChatMessage,
                                purpose,
                                cancellationToken);
                        },
                        cancellationToken);
                }
                catch (Exception ex) when (!hasAttemptedContextLengthRecovery && IsContextLengthExceeded(ex, session))
                {
                    hasAttemptedContextLengthRecovery = true;
                    if (assistantChatMessage.HasOutputContent) compressionBoundary = CompressionBoundary.Through(assistantNode);
                    var compacted = await CompactContextAsync(
                        chatContext,
                        generationContext,
                        ContextCompressionTrigger.ContextLengthRecovery,
                        compressionBoundary,
                        cancellationToken);
                    if (!compacted.HasAppliedSummary) return new ChatGenerationResult(assistantChatMessage, compacted.Error);

                    await chatContext.DispatchAsync(() =>
                    {
                        assistantChatMessage.FinishedAt = DateTimeOffset.UtcNow;
                        assistantChatMessage.IsBusy = false;
                        assistantChatMessage = new AssistantChatMessage { IsBusy = true };
                        assistantNode = generationContext.Append(assistantChatMessage);
                    });
                    continue;
                }

                await chatContext.ReportContextUsageAsync(
                    invocationResult.Usage,
                    session.Configuration.ModelId,
                    session.Configuration.ContextLimit);

                _currentModelInvocationEventId.Value = invocationResult.InvocationId;
                if (invocationResult.FunctionCalls.Count > 0)
                {
                    await InvokeFunctionsAsync(
                        generationContext,
                        chatContext,
                        assistantChatMessage,
                        invocationResult,
                        cancellationToken);
                }

                // Advance only after all tool calls from this response have their results.
                if (assistantChatMessage.HasOutputContent) compressionBoundary = CompressionBoundary.Through(assistantNode);

                var shouldCompact = !hasAttemptedAutomaticCompaction &&
                    chatContext.ContextUsage.Snapshot.HasReachedCompressionThreshold(
                        generationContext.ContextCompressionThreshold);
                if (shouldCompact)
                {
                    hasAttemptedAutomaticCompaction = true;
                    var compacted = await CompactContextAsync(
                        chatContext,
                        generationContext,
                        ContextCompressionTrigger.Automatic,
                        compressionBoundary,
                        cancellationToken);
                    if (!compacted.HasAppliedSummary) cancellationToken.ThrowIfCancellationRequested();

                    if (compacted.HasAppliedSummary && invocationResult.FunctionCalls.Count > 0)
                    {
                        await chatContext.DispatchAsync(() =>
                        {
                            assistantChatMessage.FinishedAt = DateTimeOffset.UtcNow;
                            assistantChatMessage.IsBusy = false;
                            assistantChatMessage = new AssistantChatMessage { IsBusy = true };
                            assistantNode = generationContext.Append(assistantChatMessage);
                        });
                    }
                }

                if (invocationResult.FunctionCalls.Count <= 0) break;
            }

            if (enableNotifications)
            {
                WeakReferenceMessenger.Default.Send(
                    new FlashChatWindowMessage(assistantChatMessage.Items.LastOrDefault()?.As<AssistantChatMessageTextSpan>()?.Content));
            }
        }
        catch (OperationCanceledException ex) when (IsCallerCancellation(ex, cancellationToken))
        {
            await chatContext.DispatchAsync(() => assistantChatMessage.IsCanceled = true);
            throw;
        }
        catch (Exception ex)
        {
            ex = ChatExceptionNormalizer.Handle(ex, generationContext?.Session, new ChatRequestFailureContext(cancellationToken));
            _logger.LogError(ex, "Error generating chat response");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message.Trim());

            var friendlyMessage = ex.GetFriendlyMessage();
            await chatContext.DispatchAsync(() =>
            {
                assistantChatMessage.Error = ex;
                assistantChatMessage.ErrorMessageKey = friendlyMessage;
            });

            if (enableNotifications) WeakReferenceMessenger.Default.Send(new FlashChatWindowMessage(friendlyMessage.ToString()));
        }
        finally
        {
            _currentModelInvocationEventId.Value = previousModelInvocationEventId;
            await chatContext.DispatchAsync(() =>
            {
                assistantChatMessage.FinishedAt = DateTimeOffset.UtcNow;
                assistantChatMessage.IsBusy = false;
                if (ReferenceEquals(chatContext.GenerationContext, generationContext)) chatContext.GenerationContext = null;
            });
            if (generationContext is not null)
            {
                await generationContext.DisposeAsync();
            }

            activity?.Dispose();
        }

        return new ChatGenerationResult(assistantChatMessage, assistantChatMessage.Error);
    }

    private async ValueTask EnsureVisualContextResetNoticeAsync(
        ChatContext chatContext,
        GenerationContext generationContext,
        AssistantChatMessage? assistantChatMessage,
        CancellationToken cancellationToken)
    {
        var selection = await ChatHistoryBuilder.SelectContextMessagesAsync(
            chatContext,
            generationContext.GetPath(),
            generationContext.MaxContextRounds,
            generationContext.Session.Configuration.ContextLimit,
            cancellationToken);
        var referencedContextIds = VisualContextReferenceTracker.GetActiveContextIds(selection.Messages);
        if (referencedContextIds.Count == 0) return;

        var currentVisualContextId = await _visualService.GetCurrentContextIdAsync(chatContext.VisualState, cancellationToken);
        if (referencedContextIds.All(id => id == currentVisualContextId)) return;

        var resetMessage = new VisualContextResetChatMessage(ChatVisualService.ResetNotice, currentVisualContextId);
        if (assistantChatMessage is null)
        {
            await chatContext.DispatchAsync(() => generationContext.Append(resetMessage));
            return;
        }

        await chatContext.DispatchAsync(() => assistantChatMessage.AddSpan(new AssistantChatMessageVisualContextResetSpan(resetMessage)));
    }

    private static bool IsContextLengthExceeded(Exception exception, ModelSession session) =>
        ChatExceptionNormalizer.GetFinalFailure(ChatExceptionNormalizer.Handle(exception, session))
            is HandledChatException.InvalidRequest.ContextLengthExceeded;

    private async Task<ModelInvocationResult> GetStreamingChatMessageContentsAsync(
        GenerationContext generationContext,
        ChatContext chatContext,
        IList<MEAI.ChatMessage> chatHistory,
        IList<MEAI.AITool>? toolDeclarations,
        AssistantChatMessage assistantChatMessage,
        StatisticsModelInvocationPurpose purpose,
        CancellationToken cancellationToken)
    {
        var session = generationContext.Session;
        var chatData = chatContext.Data;
        var callingToolsActivity = default(IDisposable);
        var span = default(AssistantChatMessageSpan);
        var (checkpointCount, checkpointMetadata) = await chatContext.DispatchAsync(() =>
            (assistantChatMessage.Count, assistantChatMessage.Metadata));

        var assembler = new ResponseAssembler(
            session.Dialect,
            openVisibleBuffer: (isReasoning, block) =>
            {
                if (isReasoning)
                {
                    var reasoningSpan = block.BoundSpan as AssistantChatMessageReasoningSpan ?? OpenSpan<AssistantChatMessageReasoningSpan>();
                    block.BoundSpan = reasoningSpan;
                    return reasoningSpan.ReasoningMarkdownBuilder;
                }

                var textSpan = block.BoundSpan as AssistantChatMessageTextSpan ?? OpenSpan<AssistantChatMessageTextSpan>();
                block.BoundSpan = textSpan;
                return textSpan.ContentMarkdownBuilder;
            },
            onBlockUpdated: block =>
            {
                if (block.BoundSpan is null && block is { IsReasoning: true, Metadata.Count: > 0 })
                {
                    block.BoundSpan = OpenSpan<AssistantChatMessageReasoningSpan>();
                }

                if (block is { BoundSpan: { } boundSpan, Metadata.Count: > 0 })
                {
                    boundSpan.Metadata = block.Metadata;
                }
            });

        var chatOptions = session.CreateChatOptions(
            tools: toolDeclarations,
            toolMode: toolDeclarations is { Count: > 0 } ? MEAI.ChatToolMode.Auto : null);

        await using var statistics = new RequestStatistics(
            this,
            session,
            chatData.Metadata.Id,
            assistantChatMessage.Owner?.Id,
            purpose,
            usage => chatContext.DispatchAsync(() => assistantChatMessage.UsageDetails.Accumulate(usage)));
        var retryActivity = default(IBusyActivity);

        try
        {
            var streamRequest = session.StreamRequestAsync(
                [.. chatHistory],
                chatOptions,
                cancellationToken: cancellationToken);
            await foreach (var update in streamRequest)
            {
                var hasFunctionCallUpdates = update is ChatRequestUpdate.Content callUpdate && session.HasToolCallUpdate(callUpdate.Value);
                var hasGeneratedOutput = update is ChatRequestUpdate.Content output && (HasGeneratedOutput(output.Value) || hasFunctionCallUpdates);
                await statistics.HandleAsync(update, hasGeneratedOutput, cancellationToken);
                if (hasGeneratedOutput)
                {
                    await chatContext.DispatchAsync(() => retryActivity?.Dispose());
                    retryActivity = null;
                }

                switch (update)
                {
                    case ChatRequestUpdate.AttemptFailed failed:
                    {
                        var isImageLimitFailure = IsImageRecoveryFailure(failed.Failure.Exception) &&
                            chatHistory.Any(static message => message.Contents.Any(static item => item is MEAI.DataContent or MEAI.UriContent));
                        if (failed.RetryDelay is not null || isImageLimitFailure)
                        {
                            await chatContext.DispatchAsync(() =>
                            {
                                // Only this request's provisional spans are removed. Previously completed
                                // tool cycles and their results remain on the same message.
                                assistantChatMessage.TruncateSpans(checkpointCount);
                                assistantChatMessage.Metadata = checkpointMetadata;
                                span = null;
                                assembler.Reset();
                                callingToolsActivity?.Dispose();
                                callingToolsActivity = null;
                                if (failed.RetryDelay is not null)
                                {
                                    retryActivity = UpdateRequestRetry(
                                        chatData,
                                        assistantChatMessage,
                                        session.RequestMaxRetries,
                                        retryActivity,
                                        failed);
                                }
                            });
                        }

                        continue;
                    }
                    case ChatRequestUpdate.Content content:
                    {
                        var streamingContent = content.Value;
                        var images = new Dictionary<MEAI.DataContent, FileAttachment>(ReferenceEqualityComparer.Instance);
                        foreach (var item in streamingContent.Contents)
                        {
                            if (item is not MEAI.DataContent dataContent ||
                                dataContent.Data.IsEmpty ||
                                string.IsNullOrEmpty(dataContent.MediaType) ||
                                !FileUtilities.IsOfCategory(dataContent.MediaType, FileTypeCategory.Image) ||
                                (dataContent.AdditionalProperties?.TryGetValue("thumbnail", out var isThumbnail) is true && isThumbnail is not false))
                            {
                                continue;
                            }

                            using var memoryStream = new MemoryStream(dataContent.Data.ToArray());
                            var blob = await _blobStorage.StorageBlobAsync(
                                memoryStream,
                                dataContent.MediaType,
                                cancellationToken: cancellationToken);
                            images[dataContent] = new FileAttachment(
                                new DynamicLocaleKey(string.Empty),
                                blob.LocalPath,
                                blob.Sha256,
                                blob.MimeType);
                        }

                        await chatContext.DispatchAsync(() =>
                        {
                            foreach (var item in streamingContent.Contents)
                            {
                                assembler.AppendItem(item);
                                if (item is MEAI.DataContent dataItem && images.TryGetValue(dataItem, out var attachment))
                                {
                                    assembler.EndBlock();
                                    var imageSpan = OpenSpan<AssistantChatMessageImageSpan>();
                                    imageSpan.ImageOutput = attachment;
                                }
                            }

                            assembler.ApplyBoundary(streamingContent);

                            if (callingToolsActivity is null && hasFunctionCallUpdates)
                            {
                                callingToolsActivity = chatData.StartActivity(
                                    LucideIconKind.Hammer,
                                    new DynamicLocaleKey(LocaleKey.ChatContext_BusyMessage_CallingTools),
                                    removeAfterCompletion: true,
                                    owner: assistantChatMessage);
                            }
                        });

                        break;
                    }
                }
            }

            await statistics.CompleteAsync();
        }
        catch (OperationCanceledException ex) when (IsCallerCancellation(ex, cancellationToken))
        {
            await statistics.CancelAsync();
            throw;
        }
        finally
        {
            await chatContext.DispatchAsync(() =>
            {
                retryActivity?.Dispose();
                span?.FinishedAt ??= DateTimeOffset.UtcNow;
                callingToolsActivity?.Dispose();
            });
        }

        return new ModelInvocationResult(assembler.FunctionCalls, statistics.AttemptUsage, statistics.AttemptInvocationId);

        TSpan OpenSpan<TSpan>() where TSpan : AssistantChatMessageSpan, new()
        {
            span?.FinishedAt = DateTimeOffset.UtcNow;
            var next = new TSpan();
            span = next;
            assistantChatMessage.AddSpan(next);
            return next;
        }
    }

    private async Task<(ToolResultRecord Record, Exception? Error)> InvokeFunctionAsync(
        ModelSession session,
        MEAI.FunctionCallContent content,
        FunctionCallContext context,
        ChatPluginDisplayBlock? displayBlock,
        CancellationToken cancellationToken)
    {
        var pluginName = context.ChatPlugin.Key;
        using var activity = _activitySource.StartChatActivity("execute_tool", session.Configuration);
        activity?.SetTag("gen_ai.tool.plugin", pluginName);
        activity?.SetTag("gen_ai.tool.name", content.Name);
        activity?.SetTag("gen_ai.tool.input", content.Arguments?.ToString());
        var toolInvocationId = Guid.CreateVersion7();
        var toolStartedAt = DateTimeOffset.UtcNow;
        var toolStatus = StatisticsToolInvocationStatus.Success;
        await _statisticsRecorder.StartToolInvocationAsync(
            new StatisticsToolInvocationDraft(
                toolInvocationId,
                _currentTurnEventId.Value,
                _currentModelInvocationEventId.Value,
                context.ChatContext.Data.Metadata.Id,
                pluginName,
                content.Name,
                toolStartedAt),
            cancellationToken);

        _toolCallsCounter.Add(
            1,
            new KeyValuePair<string, object?>("gen_ai.tool.plugin", pluginName),
            new KeyValuePair<string, object?>("gen_ai.tool.name", content.Name),
            new KeyValuePair<string, object?>("gen_ai.tool.is_mcp", context.ChatPlugin.IsMcp));

        ToolResultRecord record;
        var error = default(Exception);
        try
        {
            var permissionKey = context.PermissionKey;
            var consentDecision = await ProcessConsentAsync(permissionKey);
            switch (consentDecision.Kind)
            {
                case ConsentDecisionKind.AlwaysAllow:
                {
                    _settings.Plugin.ToolBypassApprovalRulesets[permissionKey] = true;
                    break;
                }
                case ConsentDecisionKind.AllowSession:
                {
                    context.ChatContext.ToolBypassApprovalRulesets[permissionKey] = true;
                    break;
                }
                case ConsentDecisionKind.Deny:
                {
                    toolStatus = context.ApprovalFailure is null ? StatisticsToolInvocationStatus.Denied : StatisticsToolInvocationStatus.Error;
                    return (new ToolResultRecord(content.Name, pluginName, content.CallId,
                        consentDecision.FormatReason("Tool execution was denied by approval.")), null);
                }
                case ConsentDecisionKind.Custom when
                    context.ChatPlugin.IsMcp &&
                    consentDecision.CustomOption?.Key is ToolConsentCustomOption.BypassMcpServerApproval:
                {
                    ToolBypassApprovalPolicy.SetPluginRule(_settings.Plugin.ToolBypassApprovalRulesets, context.ChatPlugin, true);
                    break;
                }
                case ConsentDecisionKind.Custom:
                {
                    throw new InvalidOperationException("Unknown custom tool-consent option.");
                }
            }

            var completed = await context.Binding.InvokeAsync(context.Services, content, cancellationToken);
            var output = new ToolResultRecord(content.Name, pluginName, completed.CallId, completed.Result)
            {
                Metadata = Serialization.MetadataDictionaryMessagePackFormatter.CaptureProperties(completed.AdditionalProperties)
            };
            record = await ToolResultCapture.CaptureAsync(output, _blobStorage, cancellationToken);
            if (context.ApprovalFailure is not null || record.Result is ToolContentResult { IsError: true })
            {
                toolStatus = StatisticsToolInvocationStatus.Error;
            }
            else if (context.HasApprovalDenied)
            {
                toolStatus = StatisticsToolInvocationStatus.Denied;
            }
        }
        catch (Exception ex)
        {
            toolStatus = ex is OperationCanceledException || cancellationToken.IsCancellationRequested ?
                StatisticsToolInvocationStatus.Canceled :
                context.ApprovalFailure is not null ?
                    StatisticsToolInvocationStatus.Error :
                    context.HasApprovalDenied ?
                        StatisticsToolInvocationStatus.Denied :
                        StatisticsToolInvocationStatus.Error;
            var isCallerCancellation = IsCallerCancellation(ex, cancellationToken);
            ex = HandledFunctionInvokingException.Handle(ex);
            if (!isCallerCancellation)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex, "Error invoking tool '{FunctionName}'", content.Name);
            }

            error = ex;
            record = new ToolResultRecord(content.Name, pluginName, content.CallId, new PromptTokenLimit(4096, $"Error: {ex.Message}"));
        }
        finally
        {
            await _statisticsRecorder.CompleteToolInvocationAsync(
                toolInvocationId,
                toolStatus,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        return (record, error);

        Task<ConsentDecision> ProcessConsentAsync(string permissionKey)
        {
            if (context.ApprovalMode == ToolApprovalMode.FullAccess) return Task.FromResult(ConsentDecision.AllowOnce);
            if (!_settings.Plugin.ToolBypassApprovalRulesets.TryGetValue(permissionKey, out var isPermissionGranted))
            {
                isPermissionGranted = context.IsPermissionGranted;
            }

            if (isPermissionGranted)
            {
                return Task.FromResult(ConsentDecision.AllowOnce);
            }

            FormattedDynamicLocaleKey headerKey;
            IReadOnlyList<RequestConsentCustomOption>? customOptions = null;
            if (context.ChatPlugin.IsMcp)
            {
                headerKey = new FormattedDynamicLocaleKey(LocaleKey.ChatPluginConsentRequest_MCP_Header, context.ChatFunction.HeaderKey);
                customOptions =
                [
                    new RequestConsentCustomOption(
                        ToolConsentCustomOption.BypassMcpServerApproval,
                        new FormattedDynamicLocaleKey(
                            LocaleKey.ChatPluginConsentRequest_MCP_BypassServerApproval_Header,
                            context.ChatPlugin.HeaderKey),
                        null)
                ];
            }
            else
            {
                if (context.ChatFunction is BuiltInChatFunction { OnPermissionConsent: { } onPermissionConsent })
                {
                    return onPermissionConsent(content) switch
                    {
                        true => Task.FromResult(ConsentDecision.AllowOnce),
                        false => Task.FromResult(ConsentDecision.Deny()),
                        null => Task.FromResult(ConsentDecision.AllowOnce)
                    };
                }

                if (context.ChatFunction.Permissions == ChatFunctionPermissions.None)
                {
                    headerKey = new FormattedDynamicLocaleKey(LocaleKey.ChatPluginConsentRequest_CommonNone_Header, context.ChatFunction.HeaderKey);
                }
                else
                {
                    headerKey = new FormattedDynamicLocaleKey(
                        LocaleKey.ChatPluginConsentRequest_Common_Header,
                        context.ChatFunction.HeaderKey,
                        new DirectLocaleKey(context.ChatFunction.Permissions.I18N(LocaleKey.Common_Comma.I18N(), true)));
                }
            }

            if (context.ApprovalMode == ToolApprovalMode.Auto) return ReviewOuterConsentAsync();
            return context.WaitForUserInputAsync(() => context.ChatContext.UserInterfaceBroker.HandleConsentRequestAsync(
                headerKey,
                displayBlock,
                RequestConsentRememberMasks.All,
                customOptions,
                cancellationToken));
        }

        async Task<ConsentDecision> ReviewOuterConsentAsync()
        {
            var review = await context.ReviewApprovalAsync(new ToolApprovalScope("Tool invocation"), cancellationToken);
            return review.IsAllowed ? ConsentDecision.AllowOnce : ConsentDecision.Deny(review.FormatReason());
        }
    }

    private static string GetTitleInput(UserChatMessage message)
    {
        var parts = new List<string>();
        if (message is UserStrategyChatMessage strategyMessage)
        {
            parts.Add(strategyMessage.Strategy.NameKey.ToString() ?? string.Empty);
        }

        parts.Add(message.Content.SafeSubstring(0, 1024));
        foreach (var attachment in message.Attachments)
        {
            var text = attachment switch
            {
                TextSelectionAttachment selection => selection.Text,
                TextAttachment textAttachment => textAttachment.Text,
                _ => attachment.HeaderKey.ToString() ?? string.Empty
            };
            parts.Add(text.SafeSubstring(0, 1024));
        }

        return string.Join("\n", parts.Where(static part => !string.IsNullOrWhiteSpace(part))).SafeSubstring(0, 2048);
    }

    private async Task GenerateTopicAsync(
        Assistant assistant,
        string userMessage,
        ChatContext chatContext,
        string? expectedTopic,
        CancellationToken cancellationToken)
    {
        var metadata = chatContext.Data.Metadata;
        ModelSession? session = null;
        Activity? activity = null;
        try
        {
            var titleAssistant = _assistantSpecializationResolver.Resolve(_settings.SystemAssistant.TitleGeneration, assistant);
            session = _modelSessionFactory.Create(titleAssistant);
            _chatTopicsCounter.Add(1, GetModelTag(session.Configuration.ModelId));
            activity = _activitySource.StartChatActivity("generate_topic", session.Configuration);
            var language = _settings.Display.Language.ToEnglishName();
            activity?.SetTag("id", metadata.Id);
            activity?.SetTag("user_message.length", userMessage.Length);
            activity?.SetTag("system_language", language);

            var chatHistory = new List<MEAI.ChatMessage>
            {
                new(MEAI.ChatRole.System, DefaultPrompts.TitleGeneratorSystemPrompt),
                new(
                    MEAI.ChatRole.User,
                    ScopedPromptRenderer.RenderPrompt(
                        DefaultPrompts.TitleGeneratorUserPrompt,
                        key => key switch
                        {
                            "UserMessage" => userMessage.SafeSubstring(0, 2048),
                            SystemPromptPlaceholderSource.SystemLanguageName => language,
                            _ => null
                        })),
            };
            var response = await ReadResponseAsync(
                session,
                chatHistory,
                session.CreateChatOptions(),
                metadata.Id,
                null,
                StatisticsModelInvocationPurpose.TopicGeneration,
                null,
                null,
                cancellationToken);
            var titleBuilder = new StringBuilder(response.Text);

            ReadOnlySpan<char> punctuationChars = ['.', ',', '!', '?', '。', '，', '！', '？'];
            titleBuilder.Length = Math.Min(100, titleBuilder.Length);
            for (var i = titleBuilder.Length - 1; i >= 0; i--)
            {
                if (char.IsWhiteSpace(titleBuilder[i]) || punctuationChars.Contains(titleBuilder[i])) continue;
                if (i > 0 && char.IsHighSurrogate(titleBuilder[i - 1]) && char.IsLowSurrogate(titleBuilder[i]))
                {
                    i--;
                }

                titleBuilder.Length = i + 1;
                break;
            }

            await chatContext.DispatchAsync(() =>
            {
                if (metadata.Topic == expectedTopic && titleBuilder.Length > 0)
                {
                    metadata.Rename(titleBuilder.ToString());
                }
            });
            activity?.SetTag("topic.length", metadata.Topic?.Length ?? 0);
        }
        catch (OperationCanceledException ex) when (IsCallerCancellation(ex, cancellationToken))
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Operation canceled by caller");
        }
        catch (Exception ex)
        {
            ex = ChatExceptionNormalizer.Handle(ex, session, new ChatRequestFailureContext(cancellationToken));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Failed to generate chat title");
        }
        finally
        {
            activity?.Dispose();
            session?.Dispose();
        }
    }

    private async Task RecordToolInvocationAsync(
        ChatData chatData,
        MEAI.FunctionCallContent content,
        ChatPlugin? plugin,
        StatisticsToolInvocationStatus status,
        CancellationToken cancellationToken)
    {
        var invocationId = Guid.CreateVersion7();
        var startedAt = DateTimeOffset.UtcNow;
        await _statisticsRecorder.StartToolInvocationAsync(
            new StatisticsToolInvocationDraft(
                invocationId,
                _currentTurnEventId.Value,
                _currentModelInvocationEventId.Value,
                chatData.Metadata.Id,
                plugin?.Key,
                content.Name,
                startedAt),
            cancellationToken);
        await _statisticsRecorder.CompleteToolInvocationAsync(
            invocationId,
            status,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
    }

    private async Task RecordImageAttachmentStatisticsAsync(ChatData chatData, UserChatMessage userChatMessage)
    {
        var imageAttachments = userChatMessage.Attachments
            .AsValueEnumerable()
            .OfType<FileAttachment>()
            .Where(static x => x.IsImage)
            .ToArray();
        if (imageAttachments.Length == 0) return;

        await _statisticsRecorder.RecordVisualContextAsync(
            new StatisticsVisualContextDraft(
                _currentTurnEventId.Value,
                chatData.Metadata.Id,
                StatisticsVisualContextSource.ImageAttachment,
                ImageCount: imageAttachments.Length),
            CancellationToken.None);
    }

    private IDisposable BeginStatisticsTurn(Guid? turnEventId)
    {
        var previousTurnEventId = _currentTurnEventId.Value;
        _currentTurnEventId.Value = turnEventId;
        return Disposable.Create(() => _currentTurnEventId.Value = previousTurnEventId);
    }

    #region Telemetry

    private void RecordChatUsageMetrics(ChatUsageDetails usageDetails, string? modelId)
    {
        var tag = GetModelTag(modelId);
        if (usageDetails.InputTokenCount > 0) _inputTokensHistogram.Record(usageDetails.InputTokenCount, tag);
        if (usageDetails.CachedInputTokenCount > 0) _cachedInputTokensHistogram.Record(usageDetails.CachedInputTokenCount, tag);
        if (usageDetails.OutputTokenCount > 0) _outputTokensHistogram.Record(usageDetails.OutputTokenCount, tag);
        if (usageDetails.ReasoningTokenCount > 0) _reasoningTokensHistogram.Record(usageDetails.ReasoningTokenCount, tag);
    }

    private static KeyValuePair<string, object?> GetModelTag(string? modelId) => new("gen_ai.request.model", modelId);

    #endregion

    private enum ToolConsentCustomOption
    {
        BypassMcpServerApproval
    }

    private sealed record ModelInvocationResult(
        IReadOnlyList<MEAI.FunctionCallContent> FunctionCalls,
        ChatUsageDetails Usage,
        Guid InvocationId
    );

    private sealed class ScopedPromptRenderer(
        IPromptPlaceholderSource promptPlaceholderSource,
        PromptPlaceholderContext promptPlaceholderContext
    ) : IPromptRenderer
    {
        public static string RenderPrompt(string prompt, Func<string, string?> resolver)
        {
            return PromptTemplateRenderer.Render(prompt, resolver);
        }

        public string RenderSystemPrompt(string prompt)
        {
            return PromptTemplateRenderer.Render(prompt, promptPlaceholderSource, promptPlaceholderContext);
        }

        public string RenderStrategyUserPrompt(string strategyBody, string? userInput, PreprocessorResult? preprocessorResult)
        {
            var strategySource = new CompositePromptPlaceholderSource(
            [
                StrategyPromptPlaceholderSource.Instance,
                promptPlaceholderSource
            ]);
            var strategyContext = promptPlaceholderContext with
            {
                Argument = userInput,
                Variables = preprocessorResult?.Variables
            };
            var renderedStrategy = RenderPrompt(
                strategyBody,
                key => strategySource.TryResolve(key, strategyContext, out var value) ? value : null);

            if (string.IsNullOrEmpty(userInput))
            {
                return renderedStrategy;
            }

            return new StringBuilder(renderedStrategy)
                .AppendLine()
                .AppendLine("<UserRequestStart>")
                .Append(userInput)
                .ToString();
        }
    }
}