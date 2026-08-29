using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
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
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;
using FunctionCallContent = Microsoft.SemanticKernel.FunctionCallContent;
using FunctionResultContent = Microsoft.SemanticKernel.FunctionResultContent;
using PromptTemplateRenderer = Everywhere.AI.Prompts.PromptTemplateRenderer;

namespace Everywhere.Chat;

public sealed partial class ChatService : IChatService
{
    private readonly IChatContextManager _chatContextManager;
    private readonly IChatPluginManager _chatPluginManager;
    private readonly IKernelMixinFactory _kernelMixinFactory;
    private readonly IBlobStorage _blobStorage;
    private readonly Settings _settings;
    private readonly PersistentState _persistentState;
    private readonly IPromptService _promptService;
    private readonly ISkillPromptProvider _skillPromptProvider;
    private readonly IStatisticsRecorder _statisticsRecorder;
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
        IKernelMixinFactory kernelMixinFactory,
        IBlobStorage blobStorage,
        Settings settings,
        PersistentState persistentState,
        IPromptService promptService,
        ISkillPromptProvider skillPromptProvider,
        IStatisticsRecorder statisticsRecorder,
        ILogger<ChatService> logger)
    {
        _chatContextManager = chatContextManager;
        _chatPluginManager = chatPluginManager;
        _kernelMixinFactory = kernelMixinFactory;
        _blobStorage = blobStorage;
        _settings = settings;
        _persistentState = persistentState;
        _promptService = promptService;
        _skillPromptProvider = skillPromptProvider;
        _statisticsRecorder = statisticsRecorder;
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

    public void SendMessage(UserChatMessage message)
    {
        var chatContext = _chatContextManager.Current;
        var customAssistant = _settings.Model.SelectedCustomAssistant;

        chatContext.TryExecute(
            async cancellationToken =>
            {
                using var activity = _activitySource.StartActivity();
                activity?.SetTag("chat.context.id", chatContext.Metadata.Id);

                chatContext.Add(message);
                var turnEventId = await _statisticsRecorder.RecordTurnAsync(
                    chatContext,
                    FindMessageNode(chatContext, message),
                    StatisticsTurnKind.Send,
                    cancellationToken);
                using var turnScope = BeginStatisticsTurn(turnEventId);

                if (customAssistant is null)
                {
                    chatContext.Add(CreateCustomAssistantNotSelectedErrorAssistantChatMessage());
                    return;
                }

                ProcessUserChatMessage(chatContext, message, cancellationToken);

                var assistantChatMessage = new AssistantChatMessage { IsBusy = true };
                chatContext.Add(assistantChatMessage);

                var systemPromptOverride = message.As<UserStrategyChatMessage>()?.Strategy.SystemPrompt;
                await GenerateAsync(
                    chatContext,
                    customAssistant,
                    assistantChatMessage,
                    systemPromptOverride: systemPromptOverride,
                    cancellationToken: cancellationToken);
            },
            _logger.ToExceptionHandler());
    }

    public void Edit(ChatMessageNode oldNode, UserChatMessage newMessage)
    {
        if (oldNode.Message.Role != AuthorRole.User)
        {
            throw new InvalidOperationException("Only user messages can be edited.");
        }

        var chatContext = oldNode.Context;
        var customAssistant = _settings.Model.SelectedCustomAssistant;

        chatContext.TryExecute(
            async cancellationToken =>
            {
                using var activity = _activitySource.StartActivity();
                activity?.SetTag("chat.context.id", chatContext.Metadata.Id);

                chatContext.CreateBranchOn(oldNode, newMessage);
                var turnEventId = await _statisticsRecorder.RecordTurnAsync(
                    chatContext,
                    FindMessageNode(chatContext, newMessage),
                    StatisticsTurnKind.Edit,
                    cancellationToken);
                using var turnScope = BeginStatisticsTurn(turnEventId);

                if (customAssistant is null)
                {
                    chatContext.Add(CreateCustomAssistantNotSelectedErrorAssistantChatMessage());
                    return;
                }

                ProcessUserChatMessage(chatContext, newMessage, cancellationToken);

                var assistantChatMessage = new AssistantChatMessage { IsBusy = true };
                chatContext.Add(assistantChatMessage);

                var systemPromptOverride = newMessage.As<UserStrategyChatMessage>()?.Strategy.SystemPrompt;
                await GenerateAsync(
                    chatContext,
                    customAssistant,
                    assistantChatMessage,
                    systemPromptOverride: systemPromptOverride,
                    cancellationToken: cancellationToken);
            },
            _logger.ToExceptionHandler());
    }

    public void Retry(ChatMessageNode node)
    {
        if (node.Message.Role != AuthorRole.Assistant)
        {
            throw new InvalidOperationException("Only assistant messages can be retried.");
        }

        var chatContext = node.Context;
        var customAssistant = _settings.Model.SelectedCustomAssistant;

        chatContext.TryExecute(
            async cancellationToken =>
            {
                using var activity = _activitySource.StartActivity();
                activity?.SetTag("chat.context.id", chatContext.Metadata.Id);

                if (customAssistant is null)
                {
                    chatContext.CreateBranchOn(node, CreateCustomAssistantNotSelectedErrorAssistantChatMessage());
                    return;
                }

                var assistantChatMessage = new AssistantChatMessage { IsBusy = true };
                chatContext.CreateBranchOn(node, assistantChatMessage);

                var turnEventId = await _statisticsRecorder.RecordTurnAsync(
                    chatContext,
                    FindPreviousUserNode(chatContext, node),
                    StatisticsTurnKind.Retry,
                    cancellationToken);
                using var turnScope = BeginStatisticsTurn(turnEventId);

                await GenerateAsync(chatContext, customAssistant, assistantChatMessage, cancellationToken: cancellationToken);

                static ChatMessageNode? FindPreviousUserNode(ChatContext chatContext, ChatMessageNode node)
                {
                    return chatContext.Read(list =>
                    {
                        var index = list.IndexOf(node);
                        return index <= 0 ? null : list.AsValueEnumerable().Take(index).LastOrDefault(x => x.Message is UserChatMessage);
                    });
                }
            },
            _logger.ToExceptionHandler());
    }

    public void Continue(ChatMessageNode node)
    {
        if (node.Message.Role != AuthorRole.Assistant)
        {
            throw new InvalidOperationException("Only assistant messages can be continued.");
        }

        var chatContext = node.Context;
        chatContext.Read(list =>
        {
            if (list.Count == 0 || list.IndexOf(node) != list.Count - 1)
            {
                throw new InvalidOperationException("Only last assistant message can be continued.");
            }
        });

        var customAssistant = _settings.Model.SelectedCustomAssistant;

        chatContext.TryExecute(
            async cancellationToken =>
            {
                using var activity = _activitySource.StartActivity();
                activity?.SetTag("chat.context.id", chatContext.Metadata.Id);

                if (customAssistant is null)
                {
                    chatContext.Add(CreateCustomAssistantNotSelectedErrorAssistantChatMessage());
                    return;
                }

                var assistantChatMessage = new AssistantChatMessage { IsBusy = true };
                chatContext.Add(assistantChatMessage);

                await GenerateAsync(
                    chatContext,
                    customAssistant,
                    assistantChatMessage,
                    purpose: StatisticsModelInvocationPurpose.ContinueResponse,
                    cancellationToken: cancellationToken);
            },
            _logger.ToExceptionHandler());
    }

    public void CompactContext()
    {
        var chatContext = _chatContextManager.Current;
        var assistant = _settings.Model.SelectedCustomAssistant;

        chatContext.TryExecute(
            async cancellationToken =>
            {
                using var activity = _activitySource.StartActivity();
                activity?.SetTag("chat.context.id", chatContext.Metadata.Id);

                if (assistant is null)
                {
                    chatContext.Add(CreateCustomAssistantNotSelectedErrorAssistantChatMessage());
                    return;
                }

                GenerationContext? environment = null;
                try
                {
                    environment = await CreateGenerationEnvironmentAsync(chatContext, assistant, null, cancellationToken);
                    await CompactContextAsync(
                        chatContext,
                        environment,
                        ContextCompressionTrigger.Manual,
                        ResolveCompressionBoundary(chatContext),
                        cancellationToken);
                }
                finally
                {
                    environment?.KernelMixin.Dispose();
                }
            },
            _logger.ToExceptionHandler());
    }

    /// <summary>
    /// Ensures that a custom assistant is selected. If not, adds an error message to the chat context and throws an exception.
    /// We use an error message instead of throwing an exception so that user's message will not be lost and the user will know what happened in the chat UI.
    /// </summary>
    /// <returns></returns>
    private static AssistantChatMessage CreateCustomAssistantNotSelectedErrorAssistantChatMessage() =>
        new()
        {
            ErrorMessageKey = new DynamicLocaleKey(LocaleKey.ChatService_Error_CustomAssistantNotSelected),
            FinishedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Process UserChatMessage
    /// </summary>
    /// <param name="chatContext"></param>
    /// <param name="userChatMessage"></param>
    /// <param name="cancellationToken"></param>
    private void ProcessUserChatMessage(
        ChatContext chatContext,
        UserChatMessage userChatMessage,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity();
        activity?.SetTag("chat.context.id", chatContext.Metadata.Id);

        // All VisualElementAttachment should be strongly referenced here.
        // So we have to need to check alive status before building visual tree XML.
        var visualElementAttachments = userChatMessage
            .Attachments
            .AsValueEnumerable()
            .OfType<VisualElementAttachment>()
            .ToArray();
        RecordImageAttachmentStatistics(chatContext, userChatMessage);
        if (visualElementAttachments.Length == 0) return;

        var analyzingContextMessage = new ActionChatMessage(
            LucideIconKind.TextSearch,
            LocaleKey.ActionChatMessage_Header_AnalyzingContext)
        {
            IsBusy = true
        };

        try
        {
            chatContext.Add(analyzingContextMessage);

            // Building the visual tree XML includes the following steps:
            // 1. Gather required parameters, such as max tokens, detail level, etc.
            // 2. Group the visual elements and build the XML in separate tasks.
            // 3. Populate result into VisualElementAttachment.Xml

            var approximateTokenLimit = _persistentState.VisualContextLengthLimit.ToTokenLimit();
            var detailLevel = _persistentState.VisualContextDetailLevel;

            var effectScope = _settings.ChatWindow.EnableVisualContextAnimation ?
                ServiceLocator.Resolve<VisualElementEffect>().CreateScanEffect(cancellationToken) :
                null;

            // Build and populate the XML for visual elements.
            var builtVisualElements = VisualContextBuilder.BuildAndPopulate(
                visualElementAttachments,
                approximateTokenLimit,
                chatContext.VisualElements.Count + 1,
                detailLevel,
                effectScope,
                cancellationToken);

            // Adds the visual elements to the chat context for future reference.
            chatContext.VisualElements.AddRange(builtVisualElements);
            _statisticsRecorder.RecordVisualContextAsync(
                    new StatisticsVisualContextDraft(
                        _currentTurnEventId.Value,
                        chatContext.Metadata.Id,
                        StatisticsVisualContextSource.AutomaticAttachmentProcessing,
                        ElementCount: builtVisualElements.Count),
                    CancellationToken.None)
                .Detach(IExceptionHandler.DangerouslyIgnoreAllException);

            // Then deactivate all the references, making them weak references.
            foreach (var reference in userChatMessage
                         .Attachments
                         .AsValueEnumerable()
                         .OfType<VisualElementAttachment>()
                         .Select(a => a.Element)
                         .OfType<ResilientReference<IVisualElement>>())
            {
                reference.IsActive = false;
            }

            // After this, only the chat context holds strong references to the visual elements.
        }
        catch (Exception ex)
        {
            ex = HandledChatException.Handle(ex, null);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message.Trim());
            analyzingContextMessage.ErrorMessageKey = ex.GetFriendlyMessage();
            _logger.LogError(ex, "Error analyzing visual tree");
        }
        finally
        {
            analyzingContextMessage.FinishedAt = DateTimeOffset.UtcNow;
            analyzingContextMessage.IsBusy = false;
        }
    }

    /// <summary>
    /// Kernel is very cheap to create, so we can create a new kernel for each request.
    /// This method builds the kernel based on the current settings.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="NotSupportedException"></exception>
    private async Task<Kernel> BuildKernelAsync(
        KernelMixin kernelMixin,
        ChatContext chatContext,
        Assistant assistant,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity();

        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatService>(this);
        builder.Services.AddSingleton(kernelMixin.ChatCompletionService);
        builder.Services.AddSingleton(_chatContextManager);
        builder.Services.AddSingleton(chatContext);
        builder.Services.AddSingleton(assistant);
        builder.Services.AddTransient<IChatPluginDisplaySink>(static x =>
            x.GetRequiredService<ChatContext>().FunctionCallContext.Value?.DisplaySink ??
            throw new InvalidOperationException($"No {nameof(IChatPluginDisplaySink)} is available in current function call context."));
        builder.Services.AddTransient<IChatPluginUserInterface>(static x =>
            x.GetRequiredService<ChatContext>().FunctionCallContext.Value ??
            throw new InvalidOperationException($"No {nameof(IChatPluginUserInterface)} is available in current function call context."));

        var customAssistant = assistant as CustomAssistant;
        if (kernelMixin.SupportsToolCall && (customAssistant?.IsToolCallEnabled ?? true))
        {
            var userMessage = chatContext.Read(list => list.AsValueEnumerable().Select(n => n.Message).OfType<UserChatMessage>().LastOrDefault());
            var strategyToolRulesets = userMessage?.As<UserStrategyChatMessage>()?.Strategy.ToolPatternRulesets;
            var webSearchRulesets = new ToolPatternRulesets(1)
            {
                {
                    "builtin.web",
                    new ToolFunctionPatternRulesets { { "web_search", _persistentState.IsWebSearchEnabled } }
                }
            };
            var toolRulesets = new ToolRulesetsPipeline(
            [
                customAssistant?.ToolEnablementRulesets,
                webSearchRulesets,
                strategyToolRulesets,
                chatContext.ToolPatternRulesets
            ]);

            var chatPluginScope = await _chatPluginManager.CreateScopeAsync(
                assistant,
                chatContext,
                toolRulesets,
                cancellationToken);
            builder.Services.AddSingleton(chatPluginScope);
            activity?.SetTag("plugins.count", chatPluginScope.Plugins.Count);

            foreach (var plugin in chatPluginScope.Plugins)
            {
                builder.Plugins.Add(plugin);
            }
        }

        return builder.Build();
    }

    private async Task<GenerationContext> CreateGenerationEnvironmentAsync(
        ChatContext chatContext,
        Assistant assistant,
        string? systemPromptOverride,
        CancellationToken cancellationToken)
    {
        var kernelMixin = _kernelMixinFactory.Create(assistant);
        try
        {
            var kernel = await BuildKernelAsync(kernelMixin, chatContext, assistant, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var customAssistant = assistant as CustomAssistant;
            var toolCallStatus = customAssistant?.ToolCallStatus ??
                (kernelMixin.SupportsToolCall ? ToolCallStatus.Enabled : ToolCallStatus.NotSupported);
            var promptRenderer = new ScopedPromptRenderer(
                SystemPromptPlaceholderSource.Instance,
                new PromptPlaceholderContext(
                    SkillsPromptResolver: () => _skillPromptProvider.GetPrompt(toolCallStatus),
                    WorkingDirectoryResolver: chatContext.EnsureWorkingDirectory));

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
                kernel,
                kernelMixin,
                promptRenderer,
                promptRenderer.RenderSystemPrompt(systemPromptTemplate),
                assistant.InputModalities,
                customAssistant is not null ?
                    ContextUsageSnapshot.NormalizeCompressionThresholdPercentage(customAssistant.ContextCompressionThreshold) :
                    ContextUsageSnapshot.DefaultCompressionThresholdPercentage);
        }
        catch
        {
            kernelMixin.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Generates a response for the given chat context and assistant chat message.
    /// </summary>
    /// <param name="chatContext"></param>
    /// <param name="assistant"></param>
    /// <param name="assistantChatMessage"></param>
    /// <param name="systemPromptOverride"></param>
    /// <param name="enableNotifications"></param>
    /// <param name="purpose">Statistics classification for model invocations produced by this generation.</param>
    /// <param name="cancellationToken"></param>
    public async Task GenerateAsync(
        ChatContext chatContext,
        Assistant assistant,
        AssistantChatMessage assistantChatMessage,
        string? systemPromptOverride = null,
        bool enableNotifications = true,
        StatisticsModelInvocationPurpose purpose = StatisticsModelInvocationPurpose.ChatResponse,
        CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartChatActivity("chat", assistant);
        activity?.SetTag("id", chatContext.Metadata.Id);

        GenerationContext? environment = null;
        var previousModelInvocationEventId = _currentModelInvocationEventId.Value;
        try
        {
            environment = await CreateGenerationEnvironmentAsync(
                chatContext,
                assistant,
                systemPromptOverride,
                cancellationToken);
            var kernel = environment.Kernel;
            var kernelMixin = environment.KernelMixin;
            var promptRenderer = environment.PromptRenderer;
            var systemPrompt = environment.SystemPrompt;
            var hasAttemptedAutomaticCompaction = false;
            var hasAttemptedContextLengthRecovery = false;

            if (ResolvePendingAutomaticCompressionTrigger(
                    chatContext,
                    environment.ContextCompressionThreshold) is { } pendingCompressionTrigger)
            {
                hasAttemptedAutomaticCompaction = true;
                var compacted = await CompactContextAsync(
                    chatContext,
                    environment,
                    pendingCompressionTrigger,
                    ResolveCompressionBoundary(chatContext, assistantChatMessage),
                    cancellationToken);
                if (!compacted && pendingCompressionTrigger == ContextCompressionTrigger.ContextLengthRecovery) return;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Build the chat history for the current generation.
                var chatHistory = await ChatHistoryBuilder.BuildChatHistoryAsync(
                    promptRenderer,
                    systemPrompt,
                    chatContext.Items,
                    _persistentState.MaxContextRounds,
                    assistant.InputModalities,
                    kernelMixin.ContextLimit,
                    cancellationToken);

                if (_settings.ChatWindow.AutomaticallyGenerateTitle &&
                    !chatContext.Metadata.IsTemporary && // Do not generate titles for temporary contexts.
                    chatContext.Metadata.Topic.IsNullOrEmpty() &&
                    chatHistory.Count(c => c.Role == AuthorRole.User) == 1 && // Only try when there's one user message.
                    chatHistory.FirstOrDefault(c => c.Role == AuthorRole.User)?.Content is { Length: > 0 } userMessage)
                {
                    // If the chat history only contains one user message and one assistant message,
                    // we can generate a title for the chat context.
                    GenerateTopicAsync(
                        assistant,
                        userMessage,
                        chatContext.Metadata,
                        cancellationToken).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
                }

                // Process streaming chat message contents (thinking, text, function calls, etc.)
                // It will return the function call contents for further processing.
                ModelInvocationResult invocationResult;
                try
                {
                    invocationResult = await GetStreamingChatMessageContentsAsync(
                        kernel,
                        kernelMixin,
                        chatContext,
                        chatHistory,
                        assistantChatMessage,
                        purpose,
                        cancellationToken);
                }
                catch (Exception ex) when (!hasAttemptedContextLengthRecovery && IsContextLengthExceeded(ex, kernelMixin))
                {
                    hasAttemptedContextLengthRecovery = true;
                    var compacted = await CompactContextAsync(
                        chatContext,
                        environment,
                        ContextCompressionTrigger.ContextLengthRecovery,
                        ResolveCompressionBoundary(chatContext, assistantChatMessage),
                        cancellationToken);
                    if (!compacted) return;

                    assistantChatMessage.FinishedAt = DateTimeOffset.UtcNow;
                    assistantChatMessage.IsBusy = false;
                    assistantChatMessage = new AssistantChatMessage { IsBusy = true };
                    chatContext.Add(assistantChatMessage);
                    continue;
                }
                await chatContext.ReportContextUsageAsync(
                    invocationResult.Usage,
                    kernelMixin.ModelId,
                    kernelMixin.ContextLimit);

                if (invocationResult.FunctionCalls.Count > 0)
                {
                    await InvokeFunctionsAsync(
                        kernel,
                        kernelMixin,
                        chatContext,
                        assistantChatMessage,
                        invocationResult.FunctionCalls,
                        cancellationToken);
                }

                var shouldCompact = !hasAttemptedAutomaticCompaction &&
                    chatContext.ContextUsage.Snapshot.HasReachedCompressionThreshold(
                        environment.ContextCompressionThreshold);
                if (shouldCompact)
                {
                    hasAttemptedAutomaticCompaction = true;
                    var compacted = await CompactContextAsync(
                        chatContext,
                        environment,
                        ContextCompressionTrigger.Automatic,
                        ResolveCompressionBoundary(chatContext, assistantChatMessage),
                        cancellationToken);
                    if (!compacted) cancellationToken.ThrowIfCancellationRequested();

                    if (compacted && invocationResult.FunctionCalls.Count > 0)
                    {
                        assistantChatMessage.FinishedAt = DateTimeOffset.UtcNow;
                        assistantChatMessage.IsBusy = false;
                        assistantChatMessage = new AssistantChatMessage { IsBusy = true };
                        chatContext.Add(assistantChatMessage);
                    }
                }

                if (invocationResult.FunctionCalls.Count <= 0) break;
            }

            if (enableNotifications)
                WeakReferenceMessenger.Default.Send(
                    new FlashChatWindowMessage(assistantChatMessage.Items.LastOrDefault()?.As<AssistantChatMessageTextSpan>()?.Content));
        }
        catch (Exception ex)
        {
            ex = HandledChatException.Handle(ex, environment?.KernelMixin);
            _logger.LogError(ex, "Error generating chat response");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message.Trim());

            var friendlyMessage = ex.GetFriendlyMessage();
            assistantChatMessage.ErrorMessageKey = friendlyMessage;

            if (enableNotifications) WeakReferenceMessenger.Default.Send(new FlashChatWindowMessage(friendlyMessage.ToString()));
        }
        finally
        {
            _currentModelInvocationEventId.Value = previousModelInvocationEventId;
            activity.SetChatUsageTags(assistantChatMessage.UsageDetails);
            RecordChatUsageMetrics(assistantChatMessage.UsageDetails, assistant.ModelId);
            _chatRequestsCounter.Add(1, GetModelTag(assistant.ModelId));

            assistantChatMessage.FinishedAt = DateTimeOffset.UtcNow;
            assistantChatMessage.IsBusy = false;

            environment?.KernelMixin.Dispose();
        }
    }

    private async Task<bool> CompactContextAsync(
        ChatContext chatContext,
        GenerationContext context,
        ContextCompressionTrigger trigger,
        Guid coveredThroughNodeId,
        CancellationToken cancellationToken)
    {
        var usageBefore = chatContext.ContextUsage.Snapshot;
        var compressionMessage = new ContextCompressionChatMessage(
            coveredThroughNodeId,
            context.KernelMixin.ModelId,
            DateTimeOffset.UtcNow,
            trigger,
            usageBefore.TotalTokenCount,
            context.KernelMixin.ContextLimit > 0 ? context.KernelMixin.ContextLimit : null);

        await using var compactionScope = await chatContext.BeginContextCompactionAsync();
        chatContext.Add(compressionMessage);
        var compressionMessageNodeId = FindMessageNode(chatContext, compressionMessage)?.Id
            ?? throw new InvalidOperationException("The context compression message was not added to the chat context.");

        try
        {
            var sourceNodes = SelectCompressionSourceNodes(chatContext.Items, coveredThroughNodeId);
            if (sourceNodes.Length == 0)
            {
                throw new ContextCompressionOutputException(LocaleKey.ContextCompression_Error_NoHistory);
            }

            var messages = ChatHistoryBuilder
                .SelectContextMessages(sourceNodes, _persistentState.MaxContextRounds, context.KernelMixin.ContextLimit)
                .ToList();
            var wasSourceHistoryTrimmed = false;
            string summary;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    summary = await RequestCompressionSummaryAsync(
                        chatContext,
                        context,
                        messages,
                        compressionMessageNodeId,
                        cancellationToken);
                    break;
                }
                catch (ContextCompressionOutputException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var handledException = HandledChatException.Handle(ex, context.KernelMixin);
                    if (handledException is not HandledChatException
                        {
                            ExceptionType: HandledChatExceptionType.ContextLengthExceeded
                        } || !TryTrimOldestConversationUnit(messages))
                    {
                        throw handledException;
                    }

                    wasSourceHistoryTrimmed = true;
                }
            }

            compressionMessage.Complete(summary, wasSourceHistoryTrimmed, DateTimeOffset.UtcNow);
            await chatContext.MarkContextCompactedAsync(
                context.KernelMixin.ModelId,
                context.KernelMixin.ContextLimit);
            return true;
        }
        catch (OperationCanceledException ex)
        {
            compressionMessage.Fail(ex.GetFriendlyMessage(), DateTimeOffset.UtcNow);
            _logger.LogInformation("Context compression was canceled");
            return false;
        }
        catch (ContextCompressionOutputException ex)
        {
            compressionMessage.Fail(new DynamicLocaleKey(ex.LocaleKey), DateTimeOffset.UtcNow);
            _logger.LogWarning(ex, "Context compression returned an invalid response");
            return false;
        }
        catch (Exception ex)
        {
            ex = HandledChatException.Handle(ex, context.KernelMixin);
            compressionMessage.Fail(ex.GetFriendlyMessage(), DateTimeOffset.UtcNow);
            _logger.LogError(ex, "Failed to compact chat context");
            return false;
        }
    }

    private async Task<string> RequestCompressionSummaryAsync(
        ChatContext chatContext,
        GenerationContext context,
        IReadOnlyList<ChatMessage> messages,
        Guid compressionMessageNodeId,
        CancellationToken cancellationToken)
    {
        var chatHistory = await ChatHistoryBuilder.BuildSelectedChatHistoryAsync(
            context.PromptRenderer,
            context.SystemPrompt,
            messages,
            context.InputModalities,
            cancellationToken);
        chatHistory.AddUserMessage(DefaultPrompts.ContextCompressionPrompt);

        using var activity = _activitySource.StartChatActivity("compact_context", context.KernelMixin);
        activity?.SetTag("gen_ai.messages.count", chatHistory.Count);
        var promptExecutionSettings = context.KernelMixin.GetPromptExecutionSettings(FunctionChoiceBehavior.None());
        var summaryBuilder = new StringBuilder();
        var functionCallBuilder = new FunctionCallContentBuilder();
        var usage = new ChatUsageDetails();
        var startedAt = DateTimeOffset.UtcNow;
        var invocationId = Guid.CreateVersion7();
        var previousModelInvocationEventId = _currentModelInvocationEventId.Value;
        Exception? invocationException = null;

        await _statisticsRecorder.StartModelInvocationAsync(
            new StatisticsModelInvocationDraft(
                invocationId,
                _currentTurnEventId.Value,
                chatContext.Metadata.Id,
                compressionMessageNodeId,
                StatisticsModelInvocationPurpose.ContextCompression,
                context.KernelMixin.ModelId,
                startedAt),
            cancellationToken);
        _currentModelInvocationEventId.Value = invocationId;

        try
        {
            await foreach (var content in context.KernelMixin.ChatCompletionService.GetStreamingChatMessageContentsAsync(
                               chatHistory,
                               promptExecutionSettings,
                               context.Kernel,
                               cancellationToken))
            {
                usage.Update(content);
                if (functionCallBuilder.Append(content))
                {
                    throw new ContextCompressionOutputException(LocaleKey.ContextCompression_Error_ToolCallNotAllowed);
                }

                foreach (var item in content.Items)
                {
                    switch (item)
                    {
                        case StreamingChatMessageContent { Content.Length: > 0 } chatMessageContent:
                            summaryBuilder.Append(chatMessageContent.Content);
                            break;
                        case StreamingTextContent { Text.Length: > 0 } textContent:
                            summaryBuilder.Append(textContent.Text);
                            break;
                    }
                }
            }

            if (functionCallBuilder.Build().Count > 0)
            {
                throw new ContextCompressionOutputException(LocaleKey.ContextCompression_Error_ToolCallNotAllowed);
            }

            var summary = summaryBuilder.ToString().Trim();
            if (summary.Length == 0)
            {
                throw new ContextCompressionOutputException(LocaleKey.ContextCompression_Error_EmptyResponse);
            }

            return summary;
        }
        catch (Exception ex)
        {
            invocationException = ex;
            throw;
        }
        finally
        {
            var finishedAt = DateTimeOffset.UtcNow;
            activity.SetChatUsageTags(usage);
            RecordChatUsageMetrics(usage, context.KernelMixin.ModelId);
            _chatRequestsCounter.Add(1, GetModelTag(context.KernelMixin.ModelId));
            _currentModelInvocationEventId.Value = previousModelInvocationEventId;
            await _statisticsRecorder.CompleteModelInvocationAsync(
                invocationId,
                usage,
                finishedAt,
                invocationException is null,
                invocationException is OperationCanceledException || cancellationToken.IsCancellationRequested,
                invocationException?.GetType().FullName,
                CancellationToken.None);
        }
    }

    private static ContextCompressionTrigger? ResolvePendingAutomaticCompressionTrigger(ChatContext chatContext, int contextCompressionThreshold)
    {
        var latestCompression = chatContext.Items
            .AsValueEnumerable()
            .Select(static node => node.Message)
            .OfType<ContextCompressionChatMessage>()
            .LastOrDefault();
        if (latestCompression is { NeedsAutomaticCompaction: true }) return latestCompression.Trigger;

        return chatContext.ContextUsage.Snapshot.HasReachedCompressionThreshold(contextCompressionThreshold) ?
            ContextCompressionTrigger.Automatic :
            null;
    }

    private static Guid ResolveCompressionBoundary(
        ChatContext chatContext,
        AssistantChatMessage? currentAssistantMessage = null)
    {
        var nodes = chatContext.Items;
        var boundaryIndex = nodes.Count - 1;
        if (currentAssistantMessage is not null)
        {
            boundaryIndex = FindMessageIndex(nodes, currentAssistantMessage);
            if (boundaryIndex >= 0 && currentAssistantMessage.Count > 0) return nodes[boundaryIndex].Id;

            boundaryIndex = FindPreviousCompressionSourceIndex(nodes, boundaryIndex - 1);
            if (boundaryIndex >= 0 && nodes[boundaryIndex].Message is UserChatMessage)
            {
                // Send/Edit/Retry add the current user turn before this empty assistant placeholder.
                // Keep that instruction verbatim in the suffix rather than asking the compression
                // model to interpret or rewrite it.
                boundaryIndex = FindPreviousCompressionSourceIndex(nodes, boundaryIndex - 1);
            }
        }
        else
        {
            boundaryIndex = FindPreviousCompressionSourceIndex(nodes, boundaryIndex);
        }

        return boundaryIndex >= 0 ? nodes[boundaryIndex].Id : Guid.Empty;
    }

    private static int FindMessageIndex(IReadOnlyList<ChatMessageNode> nodes, ChatMessage message)
    {
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(nodes[i].Message, message)) return i;
        }

        return -1;
    }

    private static int FindPreviousCompressionSourceIndex(IReadOnlyList<ChatMessageNode> nodes, int startIndex)
    {
        for (var i = Math.Min(startIndex, nodes.Count - 1); i >= 0; i--)
        {
            if (IsCompressionSourceMessage(nodes[i].Message)) return i;
        }

        return -1;
    }

    private static bool IsCompressionSourceMessage(ChatMessage message) =>
        message is ContextCompressionChatMessage { HasSummary: true } ||
        message.Role.Label == AuthorRole.Assistant.Label ||
        message.Role.Label == AuthorRole.User.Label ||
        message.Role.Label == AuthorRole.Developer.Label ||
        message.Role.Label == AuthorRole.System.Label ||
        message.Role.Label == AuthorRole.Tool.Label;

    private static ChatMessageNode[] SelectCompressionSourceNodes(
        IReadOnlyList<ChatMessageNode> nodes,
        Guid coveredThroughNodeId)
    {
        if (coveredThroughNodeId == Guid.Empty) return [];

        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Id == coveredThroughNodeId) return nodes.Take(i + 1).ToArray();
        }

        return [];
    }

    private static bool TryTrimOldestConversationUnit(List<ChatMessage> messages)
    {
        if (messages.Count <= 1) return false;

        var compressionIndex = messages.FindLastIndex(static message => message is ContextCompressionChatMessage { HasSummary: true });
        if (compressionIndex > 0)
        {
            // Retained user messages are intentionally placed before the prior summary. They are
            // useful verbatim context, but are the first optional history to drop when the
            // compression request itself exceeds the provider limit.
            messages.RemoveAt(0);
            return true;
        }

        var startIndex = compressionIndex + 1;
        if (startIndex >= messages.Count)
        {
            messages.RemoveAt(compressionIndex);
            return messages.Count > 0;
        }

        var firstUserIndex = messages.FindIndex(
            startIndex,
            static message => message.Role.Label == AuthorRole.User.Label && message is not ContextCompressionChatMessage);
        if (firstUserIndex < 0)
        {
            if (messages.Count - startIndex > 1)
            {
                messages.RemoveAt(startIndex);
                return true;
            }

            if (compressionIndex < 0) return false;

            messages.RemoveAt(compressionIndex);
            return true;
        }

        if (firstUserIndex > startIndex)
        {
            messages.RemoveRange(startIndex, firstUserIndex - startIndex);
            return true;
        }

        var nextUserIndex = messages.FindIndex(
            firstUserIndex + 1,
            static message => message.Role.Label == AuthorRole.User.Label && message is not ContextCompressionChatMessage);
        var removeCount = (nextUserIndex < 0 ? messages.Count : nextUserIndex) - firstUserIndex;
        if (removeCount >= messages.Count - startIndex)
        {
            if (compressionIndex < 0) return false;

            messages.RemoveAt(compressionIndex);
            return true;
        }

        messages.RemoveRange(firstUserIndex, removeCount);
        return true;
    }

    private static bool IsContextLengthExceeded(Exception exception, KernelMixin kernelMixin) =>
        HandledChatException.Handle(exception, kernelMixin) is HandledChatException
        {
            ExceptionType: HandledChatExceptionType.ContextLengthExceeded
        };

    /// <summary>
    /// Gets streaming chat message contents from the chat completion service.
    /// </summary>
    /// <param name="kernel"></param>
    /// <param name="kernelMixin"></param>
    /// <param name="chatContext"></param>
    /// <param name="chatHistory"></param>
    /// <param name="assistantChatMessage"></param>
    /// <param name="purpose"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<ModelInvocationResult> GetStreamingChatMessageContentsAsync(
        Kernel kernel,
        KernelMixin kernelMixin,
        ChatContext chatContext,
        ChatHistory chatHistory,
        AssistantChatMessage assistantChatMessage,
        StatisticsModelInvocationPurpose purpose,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartChatActivity("invoke_agent", kernelMixin);
        activity?.SetTag("gen_ai.messages.count", chatHistory.Count);

        AuthorRole? authorRole = null;
        IDisposable? callingToolsActivity = null;
        AssistantChatMessageSpan? span = null;

        var usage = new ChatUsageDetails(); // Each generation has its own usage details.
        var functionCallContentBuilder = new FunctionCallContentBuilder();
        var startTime = DateTimeOffset.UtcNow;
        DateTimeOffset? firstTokenAt = null;
        var isFirstToken = true;
        var promptExecutionSettings = kernelMixin.GetPromptExecutionSettings(
            kernelMixin.SupportsToolCall && kernel.Plugins.Count > 0 ?
                FunctionChoiceBehavior.Auto(autoInvoke: false) :
                null);

        var invocationId = Guid.CreateVersion7();
        await _statisticsRecorder.StartModelInvocationAsync(
            new StatisticsModelInvocationDraft(
                invocationId,
                _currentTurnEventId.Value,
                chatContext.Metadata.Id,
                FindMessageNode(chatContext, assistantChatMessage)?.Id,
                purpose,
                kernelMixin.ModelId,
                startTime),
            cancellationToken);
        _currentModelInvocationEventId.Value = invocationId;
        Exception? invocationException = null;

        try
        {
            await foreach (var streamingContent in kernelMixin.ChatCompletionService.GetStreamingChatMessageContentsAsync(
                               chatHistory,
                               promptExecutionSettings,
                               kernel,
                               cancellationToken))
            {
                usage.Update(streamingContent);

                // Track time to first token.
                if (isFirstToken)
                {
                    isFirstToken = false;
                    firstTokenAt = DateTimeOffset.UtcNow;
                    var ttftSeconds = (firstTokenAt.Value - startTime).TotalSeconds;
                    activity?.SetTag("gen_ai.request.ttft", ttftSeconds);
                    _timeToFirstTokenHistogram.Record(ttftSeconds, GetModelTag(kernelMixin.ModelId));
                }

                // Add persistent message-level metadata to the assistant chat message.
                if (streamingContent.Metadata is not null)
                {
                    foreach (var (key, value) in streamingContent.Metadata
                                 .AsValueEnumerable()
                                 .Where(kv => kernelMixin.IsPersistentMessageMetadataKey(kv.Key)))
                    {
                        assistantChatMessage.Metadata ??= new MetadataDictionary();
                        assistantChatMessage.Metadata[key] = value;
                    }
                }

                foreach (var item in streamingContent.Items)
                {
                    switch (item)
                    {
                        case StreamingChatMessageContent { Content.Length: > 0 } chatMessageContent:
                        {
                            HandleTextMessage(chatMessageContent.Content);
                            break;
                        }
                        case StreamingTextContent { Text.Length: > 0 } textContent:
                        {
                            HandleTextMessage(textContent.Text);
                            break;
                        }
                        case StreamingReasoningContent { Text.Length: > 0 } reasoningContent:
                        {
                            HandleReasoningMessage(reasoningContent.Text);
                            break;
                        }
                    }

                    // Handle binary content separately.
                    if (item.InnerContent is BinaryContent { Data: not null, MimeType: not null } binaryContent &&
                        FileUtilities.IsOfCategory(binaryContent.MimeType, FileTypeCategory.Image) &&
                        (binaryContent.Metadata?.TryGetValue("thumbnail", out var isThumbnail) is not true || isThumbnail is false))
                    {
                        using var memoryStream = new MemoryStream(binaryContent.Data.Value.ToArray());
                        var blob = await _blobStorage.StorageBlobAsync(memoryStream, binaryContent.MimeType, cancellationToken: cancellationToken);
                        EnsureSpan<AssistantChatMessageImageSpan>(true).ImageOutput = new FileAttachment(
                            new DynamicLocaleKey(string.Empty),
                            blob.LocalPath,
                            blob.Sha256,
                            blob.MimeType);
                    }

                    if (item.Metadata is not null && span is not null)
                    {
                        foreach (var (key, value) in item.Metadata
                                     .AsValueEnumerable()
                                     .Where(kv => kernelMixin.IsPersistentSpanMetadataKey(kv.Key)))
                        {
                            span.Metadata ??= new MetadataDictionary();
                            span.Metadata[key] = value;
                        }
                    }

                    void HandleTextMessage(string text)
                    {
                        EnsureSpan<AssistantChatMessageTextSpan>(false).ContentMarkdownBuilder.Append(text);
                    }

                    void HandleReasoningMessage(string text)
                    {
                        EnsureSpan<AssistantChatMessageReasoningSpan>(false).ReasoningMarkdownBuilder.Append(text);
                    }
                }

                authorRole ??= streamingContent.Role;
                var hasFunctionCallUpdates = functionCallContentBuilder.Append(streamingContent);

                if (callingToolsActivity is null && hasFunctionCallUpdates)
                {
                    callingToolsActivity = await chatContext.SetBusyActivityAsync(
                        LucideIconKind.Hammer,
                        new DynamicLocaleKey(LocaleKey.ChatContext_BusyMessage_CallingTools),
                        removeAfterCompletion: true);
                }
            }
        }
        catch (Exception ex)
        {
            invocationException = ex;
            throw;
        }
        finally
        {
            var generationEndTime = DateTimeOffset.UtcNow;
            var generationSeconds = firstTokenAt.HasValue ? Math.Max((generationEndTime - firstTokenAt.Value).TotalSeconds, 0) : 0;
            var invocationUsage = new ChatUsageDetails();
            invocationUsage.Accumulate(usage, generationSeconds);

            assistantChatMessage.UsageDetails.Accumulate(usage, generationSeconds); // Accumulate usage details.

            activity.SetChatUsageTags(usage);
            RecordChatUsageMetrics(usage, kernelMixin.ModelId);

            if (assistantChatMessage.Spans is { Count: > 0 } spans)
                spans[^1].FinishedAt ??= generationEndTime;

            callingToolsActivity?.Dispose();
            await _statisticsRecorder.CompleteModelInvocationAsync(
                invocationId,
                invocationUsage,
                generationEndTime,
                invocationException is null,
                invocationException is OperationCanceledException || cancellationToken.IsCancellationRequested,
                invocationException?.GetType().FullName,
                CancellationToken.None);
        }

        var functionCallContents = functionCallContentBuilder.Build();
        activity?.SetTag("gen_ai.tool.count", functionCallContents.Count);
        return new ModelInvocationResult(functionCallContents, usage);

        TSpan EnsureSpan<TSpan>(bool createNew) where TSpan : AssistantChatMessageSpan, new()
        {
            // Handle existing span.
            if (span is not null)
            {
                // If the existing span is of the requested type and we don't need to create a new one, return it.
                if (!createNew && span is TSpan existingSpan)
                {
                    return existingSpan;
                }

                // Finish the existing span.
                span.FinishedAt = DateTimeOffset.UtcNow;
            }

            // Create a new span of the requested type.
            TSpan newSpan;
            span = newSpan = new TSpan();
            assistantChatMessage.AddSpan(span);
            return newSpan;
        }
    }

    /// <summary>
    /// Invokes the functions specified in the function call contents.
    /// This will group the function calls by plugin and function, and invoke them sequentially.
    /// </summary>
    /// <param name="kernel"></param>
    /// <param name="kernelMixin"></param>
    /// <param name="chatContext"></param>
    /// <param name="assistantChatMessage"></param>
    /// <param name="functionCallContents"></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="InvalidOperationException"></exception>
    private async Task InvokeFunctionsAsync(
        Kernel kernel,
        KernelMixin kernelMixin,
        ChatContext chatContext,
        AssistantChatMessage assistantChatMessage,
        IReadOnlyList<FunctionCallContent> functionCallContents,
        CancellationToken cancellationToken)
    {
        // Group function calls by plugin name, and create ActionChatMessages for each group.
        // For example:
        // AI calls multiple functions at once:
        // {
        //   "function_calls": [
        //     { "function_name": "Function1", "parameters": { ... } },
        //     { "function_name": "Function1", "parameters": { ... } },
        //     { "function_name": "Function2", "parameters": { ... } }
        //   ]
        // }
        //
        // So we group them into:
        // - Function1
        //   - Call1
        //   - Call2
        // - Function2
        //   - Call1
        //
        // And invoke them one by one.
        // TODO: parallel invoke?
        var chatPluginScope = kernel.Services.GetService<IChatPluginScope>();
        var functionCallSpan = new AssistantChatMessageFunctionCallSpan();
        assistantChatMessage.AddSpan(functionCallSpan);

        try
        {
            foreach (var functionCallContentGroup in functionCallContents.GroupBy(f => f.FunctionName))
            {
                // 1. Grouped by function name.
                // After grouping, we need to find the corresponding plugin and function.
                // For example, in the above example,
                // 1st functionCallContentGroup: Key = "Function1", Values = [Call1, Call2]
                // 2nd functionCallContentGroup: Key = "Function2", Values = [Call1]

                cancellationToken.ThrowIfCancellationRequested();

                // functionCallContentGroup.Key is the function name.
                if (chatPluginScope is null)
                {
                    // Function calling is not enabled
                    // Display error in the chat span (UI).
                    var errorFunctionMessage = new FunctionCallChatMessage(
                        LucideIconKind.X,
                        new DirectLocaleKey(functionCallContentGroup.Key));
                    functionCallSpan.Add(errorFunctionMessage);

                    // Iterate through the function call contents in the group.
                    // Add the error message for each function call.
                    foreach (var functionCallContent in functionCallContentGroup)
                    {
                        // Add the function call content to the missing function chat message for DB storage.
                        errorFunctionMessage.AddCall(functionCallContent);

                        // Create the corresponding function result content with the error message.
                        var missingFunctionResultContent = new FunctionResultContent(
                            functionCallContent,
                            "Tool calling is disabled by the user");

                        // Add the function result content to the missing function chat message for DB storage.
                        errorFunctionMessage.AddResult(missingFunctionResultContent);
                        await RecordToolInvocationAsync(
                            chatContext,
                            functionCallContent,
                            null,
                            StatisticsToolInvocationStatus.Disabled,
                            cancellationToken);
                    }

                    errorFunctionMessage.ErrorMessageKey = new FormattedDynamicLocaleKey(
                        LocaleKey.HandledFunctionInvokingException_FunctionCallingDisabled,
                        new DirectLocaleKey(functionCallContentGroup.Key));

                    continue;
                }

                if (!chatPluginScope.TryGetPluginAndFunction(
                        functionCallContentGroup.Key,
                        out var chatPlugin,
                        out var chatFunction,
                        out var similarFunctionNames))
                {
                    // Not found the function, tell AI.

                    var errorMessageBuilder = new StringBuilder();
                    errorMessageBuilder.Append("Tool '").Append(functionCallContentGroup.Key).Append("' is not available.");

                    if (similarFunctionNames.Count > 0)
                    {
                        errorMessageBuilder.Append(" Did you mean:");
                        foreach (var similarFunctionName in similarFunctionNames)
                        {
                            errorMessageBuilder.Append(' ').AppendLine(similarFunctionName);
                        }
                    }

                    // Display error in the chat span (UI).
                    var errorFunctionMessage = new FunctionCallChatMessage(
                        LucideIconKind.X,
                        new DirectLocaleKey(functionCallContentGroup.Key));
                    functionCallSpan.Add(errorFunctionMessage);

                    // Iterate through the function call contents in the group.
                    // Add the error message for each function call.
                    foreach (var functionCallContent in functionCallContentGroup)
                    {
                        // Add the function call content to the missing function chat message for DB storage.
                        errorFunctionMessage.AddCall(functionCallContent);

                        // Create the corresponding function result content with the error message.
                        var missingFunctionResultContent = new FunctionResultContent(functionCallContent, errorMessageBuilder.ToString());

                        // Add the function result content to the missing function chat message for DB storage.
                        errorFunctionMessage.AddResult(missingFunctionResultContent);
                        await RecordToolInvocationAsync(
                            chatContext,
                            functionCallContent,
                            null,
                            StatisticsToolInvocationStatus.NotFound,
                            cancellationToken);
                    }

                    errorFunctionMessage.ErrorMessageKey = new FormattedDynamicLocaleKey(
                        LocaleKey.HandledFunctionInvokingException_FunctionNotFound,
                        new DirectLocaleKey(functionCallContentGroup.Key));

                    continue;
                }

                var functionCallChatMessage = new FunctionCallChatMessage(
                    chatFunction.Icon ?? chatPlugin.Icon ?? LucideIconKind.Hammer,
                    chatFunction.HeaderKey);
                functionCallChatMessage.IsBusy = true;
                functionCallSpan.Add(functionCallChatMessage); // functionCallSpan will dispose FunctionCallChatMessage

                try
                {
                    // Iterate through the function call contents in the group.
                    foreach (var functionCallContent in functionCallContentGroup)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // This should be processed in KernelMixin.
                        // All function calls must have an ID (returned from the LLM, or generated by us).
                        if (functionCallContent.Id.IsNullOrEmpty())
                        {
                            // This should never happen.
                            throw new InvalidOperationException("Tool call must have an ID");
                        }

                        // Each FunctionCallContent receives its own ambient context even though the
                        // visible FunctionCallChatMessage may aggregate calls to the same function.
                        // This keeps AsyncLocal services and runtime previews invocation-local. It
                        // does not by itself make the shared Calls/Results collections safe for a
                        // future parallel execution strategy; those collections have their own
                        // concurrency boundary.
                        using var functionCallContext = new FunctionCallContext(
                            kernel,
                            chatContext,
                            chatPlugin,
                            chatFunction,
                            functionCallChatMessage,
                            functionCallContent,
                            _settings.Plugin.ToolBypassApprovalRulesets);
                        using var functionCallContextScope = chatContext.EnterFunctionCallContext(functionCallContext);

                        // Add the function call content to the function call chat message.
                        // This will record the function call in the database.
                        functionCallChatMessage.AddCall(functionCallContent);

                        // Also add a display block for the function call content.
                        // This will allow the UI to display the function call content.
                        var friendlyContent = chatFunction.GetFriendlyCallContent(functionCallContent);
                        if (friendlyContent is not null) functionCallContext.DisplaySink.AppendBlock(friendlyContent);

                        var resultContent = await InvokeFunctionAsync(
                            kernelMixin,
                            functionCallContent,
                            functionCallContext,
                            friendlyContent,
                            cancellationToken);

                        // Try to cancel if requested immediately after function invocation (a long-time await).
                        cancellationToken.ThrowIfCancellationRequested();

                        // dd the function result content to the function call chat message.
                        // This will record the function result in the database.
                        functionCallChatMessage.AddResult(resultContent);

                        if (resultContent.InnerContent is Exception ex)
                        {
                            functionCallChatMessage.ErrorMessageKey = ex.GetFriendlyMessage();
                            break; // If an error occurs, we stop processing further function calls.
                        }
                    }
                }
                finally
                {
                    functionCallChatMessage.FinishedAt = DateTimeOffset.UtcNow;
                    functionCallChatMessage.IsBusy = false;

                    if (cancellationToken.IsCancellationRequested)
                    {
                        functionCallChatMessage.ErrorMessageKey ??= new DynamicLocaleKey(LocaleKey.FriendlyExceptionMessage_OperationCanceled);
                    }
                }
            }
        }
        finally
        {
            functionCallSpan.FinishedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task<FunctionResultContent> InvokeFunctionAsync(
        KernelMixin kernelMixin,
        FunctionCallContent content,
        FunctionCallContext context,
        ChatPluginDisplayBlock? displayBlock,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartChatActivity("execute_tool", kernelMixin);
        activity?.SetTag("gen_ai.tool.plugin", content.PluginName);
        activity?.SetTag("gen_ai.tool.name", content.FunctionName);
        activity?.SetTag("gen_ai.tool.input", content.Arguments?.ToString());
        var toolInvocationId = Guid.CreateVersion7();
        var toolStartedAt = DateTimeOffset.UtcNow;
        var toolStatus = StatisticsToolInvocationStatus.Success;
        await _statisticsRecorder.StartToolInvocationAsync(
            new StatisticsToolInvocationDraft(
                toolInvocationId,
                _currentTurnEventId.Value,
                _currentModelInvocationEventId.Value,
                context.ChatContext.Metadata.Id,
                context.ChatPlugin.Key,
                content.FunctionName,
                toolStartedAt),
            cancellationToken);

        // We don't collect input arguments in metrics because they may contain sensitive information.
        _toolCallsCounter.Add(
            1,
            new KeyValuePair<string, object?>("gen_ai.tool.plugin", content.PluginName),
            new KeyValuePair<string, object?>("gen_ai.tool.name", content.FunctionName),
            new KeyValuePair<string, object?>("gen_ai.tool.is_mcp", context.ChatPlugin is McpChatPlugin));

        FunctionResultContent resultContent;
        try
        {
            // Check permissions. If permissions are not granted, request user consent.
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
                    toolStatus = StatisticsToolInvocationStatus.Denied;
                    return new FunctionResultContent(content, consentDecision.FormatReason("Tool execution denied by user."));
                }
                case ConsentDecisionKind.Custom when
                    context.ChatPlugin is McpChatPlugin mcpPlugin &&
                    consentDecision.CustomOption?.Key is ToolConsentCustomOption.BypassMcpServerApproval:
                {
                    ToolBypassApprovalPolicy.SetPluginRule(_settings.Plugin.ToolBypassApprovalRulesets, mcpPlugin, true);
                    break;
                }
                case ConsentDecisionKind.Custom:
                {
                    throw new InvalidOperationException("Unknown custom tool-consent option.");
                }
            }

            resultContent = await content.InvokeAsync(context.Kernel, cancellationToken);
        }
        catch (Exception ex)
        {
            toolStatus = ex is OperationCanceledException || cancellationToken.IsCancellationRequested ?
                StatisticsToolInvocationStatus.Canceled :
                StatisticsToolInvocationStatus.Error;
            ex = HandledFunctionInvokingException.Handle(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Error invoking tool '{FunctionName}'", content.FunctionName);

            resultContent = new FunctionResultContent(content, new PromptTokenLimit(4096, $"Error: {ex.Message}")) { InnerContent = ex };
        }
        finally
        {
            await _statisticsRecorder.CompleteToolInvocationAsync(
                toolInvocationId,
                toolStatus,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        return resultContent;

        Task<ConsentDecision> ProcessConsentAsync(string permissionKey)
        {
            // Check if the permission is already granted in the current chat context
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
                        null => Task.FromResult(ConsentDecision.AllowOnce) // Default to allow once
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
                        new DirectLocaleKey(context.ChatFunction.Permissions.I18N(LocaleResolver.Common_Comma, true)));
                }
            }

            // The function requires permissions that are not granted.
            return context.WaitForUserInputAsync(() => context.ChatContext.UserInterfaceBroker.HandleConsentRequestAsync(
                headerKey,
                displayBlock,
                RequestConsentRememberMasks.All,
                customOptions,
                cancellationToken));
        }
    }

    private async Task GenerateTopicAsync(Assistant assistant, string userMessage, ChatContextMetadata metadata, CancellationToken cancellationToken)
    {
        if (!metadata.IsGeneratingTopic.FlipIfFalse())
        {
            // Another generation is in progress, skip generating title to avoid token waste and confusion.
            return;
        }

        KernelMixin kernelMixin;
        try
        {
            var systemAssistant = _settings.SystemAssistant.TitleGeneration.Resolve(assistant);
            kernelMixin = _kernelMixinFactory.Create(systemAssistant);
        }
        catch (Exception ex)
        {
            ex = HandledChatException.Handle(ex, null);
            _logger.LogError(ex, "Failed to resolve assistant");
            return;
        }

        _chatTopicsCounter.Add(1, GetModelTag(kernelMixin.ModelId));
        using var activity = _activitySource.StartChatActivity("generate_topic", kernelMixin);
        var startedAt = DateTimeOffset.UtcNow;
        var invocationId = Guid.CreateVersion7();
        var usage = new ChatUsageDetails();
        Exception? invocationException = null;
        await _statisticsRecorder.StartModelInvocationAsync(
            new StatisticsModelInvocationDraft(
                invocationId,
                null,
                metadata.Id,
                null,
                StatisticsModelInvocationPurpose.TopicGeneration,
                kernelMixin.ModelId,
                startedAt),
            cancellationToken);
        try
        {
            var language = _settings.Display.Language.ToEnglishName();
            activity?.SetTag("id", metadata.Id);
            activity?.SetTag("user_message.length", userMessage.Length);
            activity?.SetTag("system_language", language);

            var chatHistory = new ChatHistory
            {
                new ChatMessageContent(
                    AuthorRole.System,
                    DefaultPrompts.TitleGeneratorSystemPrompt),
                new ChatMessageContent(
                    AuthorRole.User,
                    ScopedPromptRenderer.RenderPrompt(
                        DefaultPrompts.TitleGeneratorUserPrompt,
                        key => key switch
                        {
                            "UserMessage" => userMessage.SafeSubstring(0, 2048),
                            SystemPromptPlaceholderSource.SystemLanguageName => language,
                            _ => null
                        })),
            };
            var titleBuilder = new StringBuilder();

            await foreach (var content in kernelMixin.ChatCompletionService.GetStreamingChatMessageContentsAsync(
                               chatHistory,
                               kernelMixin.GetPromptExecutionSettings(),
                               cancellationToken: cancellationToken))
            {
                usage.Update(content);

                if (content.Role == AuthorRole.Assistant)
                {
                    foreach (var item in content.Items.AsValueEnumerable().OfType<StreamingTextContent>())
                    {
                        titleBuilder.Append(item);
                    }
                }
            }

            activity.SetChatUsageTags(usage);
            RecordChatUsageMetrics(usage, kernelMixin.ModelId);

            ReadOnlySpan<char> punctuationChars = ['.', ',', '!', '?', '。', '，', '！', '？'];
            titleBuilder.Length = Math.Min(100, titleBuilder.Length); // Limit the title length to 100 characters to avoid excessively long titles.
            for (var i = titleBuilder.Length - 1; i >= 0; i--)
            {
                if (char.IsWhiteSpace(titleBuilder[i]) || punctuationChars.Contains(titleBuilder[i])) continue;

                // Truncate the title at the last non-whitespace and non-punctuation character to avoid ending with incomplete words or punctuation.
                titleBuilder.Length = i + 1;
                break;
            }

            metadata.Topic = titleBuilder.Length > 0 ? titleBuilder.ToString() : null;
            activity?.SetTag("topic.length", metadata.Topic?.Length ?? 0);
        }
        catch (Exception ex)
        {
            invocationException = ex;
            ex = HandledChatException.Handle(ex, kernelMixin);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Failed to generate chat title");
        }
        finally
        {
            await _statisticsRecorder.CompleteModelInvocationAsync(
                invocationId,
                usage,
                DateTimeOffset.UtcNow,
                invocationException is null,
                invocationException is OperationCanceledException || cancellationToken.IsCancellationRequested,
                invocationException?.GetType().FullName,
                CancellationToken.None);
            metadata.IsGeneratingTopic.FlipIfTrue();
        }
    }

    private async Task RecordToolInvocationAsync(
        ChatContext chatContext,
        FunctionCallContent content,
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
                chatContext.Metadata.Id,
                plugin?.Key,
                content.FunctionName,
                startedAt),
            cancellationToken);
        await _statisticsRecorder.CompleteToolInvocationAsync(
            invocationId,
            status,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
    }

    private void RecordImageAttachmentStatistics(ChatContext chatContext, UserChatMessage userChatMessage)
    {
        var imageAttachments = userChatMessage.Attachments
            .AsValueEnumerable()
            .OfType<FileAttachment>()
            .Where(x => x.IsImage)
            .ToArray();
        if (imageAttachments.Length == 0) return;

        _statisticsRecorder.RecordVisualContextAsync(
                new StatisticsVisualContextDraft(
                    _currentTurnEventId.Value,
                    chatContext.Metadata.Id,
                    StatisticsVisualContextSource.ImageAttachment,
                    ImageCount: imageAttachments.Length),
                CancellationToken.None)
            .Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    private IDisposable BeginStatisticsTurn(Guid? turnEventId)
    {
        var previousTurnEventId = _currentTurnEventId.Value;
        _currentTurnEventId.Value = turnEventId;
        return Disposable.Create(() => _currentTurnEventId.Value = previousTurnEventId);
    }

    private static ChatMessageNode? FindMessageNode(ChatContext chatContext, ChatMessage message) =>
        chatContext.GetAllNodes().FirstOrDefault(x => ReferenceEquals(x.Message, message));

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

    private sealed record GenerationContext(
        Kernel Kernel,
        KernelMixin KernelMixin,
        ScopedPromptRenderer PromptRenderer,
        string SystemPrompt,
        Modalities InputModalities,
        int ContextCompressionThreshold
    );

    private sealed record ModelInvocationResult(
        IReadOnlyList<FunctionCallContent> FunctionCalls,
        ChatUsageDetails Usage
    );

    private sealed class ContextCompressionOutputException(string localeKey) : Exception
    {
        public string LocaleKey { get; } = localeKey;
    }

    private sealed class ScopedPromptRenderer(
        IPromptPlaceholderSource promptPlaceholderSource,
        PromptPlaceholderContext promptPlaceholderContext
    ) : IPromptRenderer
    {
        public static string RenderPrompt(string prompt, Func<string, string?> resolver) =>
            PromptTemplateRenderer.Render(prompt, resolver);

        public string RenderSystemPrompt(string prompt)
        {
            return RenderPrompt(prompt, ResolveSharedPlaceholder);
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

        private string? ResolveSharedPlaceholder(string key) =>
            promptPlaceholderSource.TryResolve(key, promptPlaceholderContext, out var value) ? value : null;
    }
}