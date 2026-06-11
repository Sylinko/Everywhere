using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using DynamicData;
using Everywhere.AI;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.Skills;
using Everywhere.Storage;
using Everywhere.StrategyEngine;
using Everywhere.Utilities;
using Everywhere.Views;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ZLinq;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;
using FunctionCallContent = Microsoft.SemanticKernel.FunctionCallContent;
using FunctionResultContent = Microsoft.SemanticKernel.FunctionResultContent;

namespace Everywhere.Chat;

public sealed partial class ChatService : IChatService
{
    private readonly IChatContextManager _chatContextManager;
    private readonly IChatPluginManager _chatPluginManager;
    private readonly IKernelMixinFactory _kernelMixinFactory;
    private readonly IBlobStorage _blobStorage;
    private readonly Settings _settings;
    private readonly PersistentState _persistentState;
    private readonly ISkillPromptProvider _skillPromptProvider;
    private readonly ILogger<ChatService> _logger;

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
        ISkillPromptProvider skillPromptProvider,
        ILogger<ChatService> logger)
    {
        _chatContextManager = chatContextManager;
        _chatPluginManager = chatPluginManager;
        _kernelMixinFactory = kernelMixinFactory;
        _blobStorage = blobStorage;
        _settings = settings;
        _persistentState = persistentState;
        _skillPromptProvider = skillPromptProvider;
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

                await GenerateAsync(chatContext, customAssistant, assistantChatMessage, cancellationToken: cancellationToken);
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
        var branchNodes = chatContext.Items;
        if (branchNodes.Count == 0 || branchNodes.IndexOf(node) != branchNodes.Count - 1)
        {
            throw new InvalidOperationException("Only last assistant message can be continued.");
        }

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

                await GenerateAsync(chatContext, customAssistant, assistantChatMessage, cancellationToken: cancellationToken);
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
            ErrorMessageKey = new DynamicResourceKey(LocaleKey.ChatService_Error_CustomAssistantNotSelected),
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
            .ToList();

        if (visualElementAttachments.Count == 0) return;

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

        if (kernelMixin.SupportsToolCall && _persistentState.IsToolCallEnabled)
        {
            var userMessage = chatContext.Items.AsValueEnumerable().Select(n => n.Message).OfType<UserChatMessage>().LastOrDefault();
            var strategyToolRulesets = userMessage?.As<UserStrategyChatMessage>()?.Strategy.ToolRulesets;
            var toolRulesets = new ToolRulesets(1) { { "builtin.web.web_search", _persistentState.IsWebSearchEnabled } }
                .Union(strategyToolRulesets)
                .Union(chatContext.ToolRulesets);

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

    /// <summary>
    /// Generates a response for the given chat context and assistant chat message.
    /// </summary>
    /// <param name="chatContext"></param>
    /// <param name="assistant"></param>
    /// <param name="assistantChatMessage"></param>
    /// <param name="systemPromptOverride"></param>
    /// <param name="enableNotifications"></param>
    /// <param name="cancellationToken"></param>
    public async Task GenerateAsync(
        ChatContext chatContext,
        Assistant assistant,
        AssistantChatMessage assistantChatMessage,
        string? systemPromptOverride = null,
        bool enableNotifications = true,
        CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartChatActivity("chat", assistant);
        activity?.SetTag("id", chatContext.Metadata.Id);

        KernelMixin? kernelMixin = null;
        try
        {
            kernelMixin = _kernelMixinFactory.Create(assistant);
            var kernel = await BuildKernelAsync(kernelMixin, chatContext, assistant, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // Because the custom assistant maybe changed, we need to re-render the system prompt.
            // But we only do this once per generation, even if the system time may change during function calls.
            // This can save prompt tokens because they may be cached by LLM providers.
            var promptVariables = chatContext.GetPromptVariables();
            promptVariables["SkillsPrompt"] = _skillPromptProvider.GetPrompt;
            var promptRenderer = new ScopedPromptRenderer(promptVariables);
            var promptTemplate = systemPromptOverride ??
                (assistant is ISystemPromptProvider { SystemPrompt: { Length: > 0 } providedSystemPrompt } ?
                    providedSystemPrompt :
                    Prompts.DefaultSystemPrompt);
            var systemPrompt = promptRenderer.RenderSystemPrompt(promptTemplate);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Build the chat history for the current generation.
                var chatHistory = await ChatHistoryBuilder.BuildChatHistoryAsync(
                    promptRenderer,
                    systemPrompt,
                    chatContext
                        .Items
                        .AsValueEnumerable()
                        .Select(n => n.Message)
                        .Where(m => m.Role.Label is "assistant" or "user" or "tool")
                        .ToList(),
                    _persistentState.MaxContextRounds,
                    assistant.InputModalities,
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
                var functionCallContents = await GetStreamingChatMessageContentsAsync(
                    kernel,
                    kernelMixin,
                    chatContext,
                    chatHistory,
                    assistantChatMessage,
                    cancellationToken);
                if (functionCallContents.Count <= 0) break; // No more function calls, exit the loop.

                // Invoke the functions specified in the function call contents.
                await InvokeFunctionsAsync(
                    kernel,
                    kernelMixin,
                    chatContext,
                    assistantChatMessage,
                    functionCallContents,
                    cancellationToken);
            }

            if (enableNotifications)
                WeakReferenceMessenger.Default.Send(
                    new FlashChatWindowMessage(assistantChatMessage.Items.LastOrDefault()?.As<AssistantChatMessageTextSpan>()?.Content));
        }
        catch (Exception ex)
        {
            ex = HandledChatException.Handle(ex, kernelMixin);
            _logger.LogError(ex, "Error generating chat response");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message.Trim());

            var friendlyMessage = ex.GetFriendlyMessage();
            assistantChatMessage.ErrorMessageKey = friendlyMessage;

            if (enableNotifications) WeakReferenceMessenger.Default.Send(new FlashChatWindowMessage(friendlyMessage.ToString()));
        }
        finally
        {
            activity.SetChatUsageTags(assistantChatMessage.UsageDetails);
            RecordChatUsageMetrics(assistantChatMessage.UsageDetails, assistant.ModelId);
            _chatRequestsCounter.Add(1, GetModelTag(assistant.ModelId));

            assistantChatMessage.FinishedAt = DateTimeOffset.UtcNow;
            assistantChatMessage.IsBusy = false;

            kernelMixin?.Dispose();
        }
    }

    /// <summary>
    /// Gets streaming chat message contents from the chat completion service.
    /// </summary>
    /// <param name="kernel"></param>
    /// <param name="kernelMixin"></param>
    /// <param name="chatContext"></param>
    /// <param name="chatHistory"></param>
    /// <param name="assistantChatMessage"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<IReadOnlyList<FunctionCallContent>> GetStreamingChatMessageContentsAsync(
        Kernel kernel,
        KernelMixin kernelMixin,
        ChatContext chatContext,
        ChatHistory chatHistory,
        AssistantChatMessage assistantChatMessage,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartChatActivity("invoke_agent", kernelMixin);
        activity?.SetTag("gen_ai.messages.count", chatHistory.Count);

        AuthorRole? authorRole = null;
        IDisposable? callingToolsBusyMessage = null;
        AssistantChatMessageSpan? span = null;

        var usage = new ChatUsageDetails(); // Each generation has its own usage details.
        var functionCallContentBuilder = new FunctionCallContentBuilder();
        var startTime = DateTimeOffset.UtcNow;
        DateTimeOffset? firstTokenAt = null;
        var isFirstToken = true;
        var promptExecutionSettings = kernelMixin.GetPromptExecutionSettings(
            kernelMixin.SupportsToolCall && _persistentState.IsToolCallEnabled ? FunctionChoiceBehavior.Auto(autoInvoke: false) : null);

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
                            new DynamicResourceKey(string.Empty),
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

                    void HandleTextMessage(string text) => Dispatcher.UIThread.Post(() =>
                        EnsureSpan<AssistantChatMessageTextSpan>(false).ContentMarkdownBuilder.Append(text));

                    void HandleReasoningMessage(string text) => Dispatcher.UIThread.Post(() =>
                        EnsureSpan<AssistantChatMessageReasoningSpan>(false).ReasoningMarkdownBuilder.Append(text));
                }

                authorRole ??= streamingContent.Role;
                var hasFunctionCallUpdates = functionCallContentBuilder.Append(streamingContent);

                if (callingToolsBusyMessage is null && hasFunctionCallUpdates)
                {
                    callingToolsBusyMessage = chatContext.SetBusyMessage(new DynamicResourceKey(LocaleKey.ChatContext_BusyMessage_CallingTools));
                }
            }
        }
        finally
        {
            var generationEndTime = DateTimeOffset.UtcNow;
            var generationSeconds = firstTokenAt.HasValue ? Math.Max((generationEndTime - firstTokenAt.Value).TotalSeconds, 0) : 0;

            assistantChatMessage.UsageDetails.Accumulate(usage, generationSeconds); // Accumulate usage details.

            activity.SetChatUsageTags(usage);
            RecordChatUsageMetrics(usage, kernelMixin.ModelId);

            // Post to UI thread to ensure all pending text/reasoning Dispatcher.UIThread.Post
            // callbacks (which create spans and append content) have executed before we
            // signal completion.  Otherwise we race: the finally runs on the thread pool
            // while span may still be null because the Post lambdas haven't fired yet.
            Dispatcher.UIThread.Post(() =>
            {
                if (assistantChatMessage.Spans is { Count: > 0 } spans)
                    spans[^1].FinishedAt ??= generationEndTime;
            });

            callingToolsBusyMessage?.Dispose();
        }

        var functionCallContents = functionCallContentBuilder.Build();
        activity?.SetTag("gen_ai.tool.count", functionCallContents.Count);
        return functionCallContents;

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
                        new DirectResourceKey(functionCallContentGroup.Key));
                    functionCallSpan.Add(errorFunctionMessage);

                    // Iterate through the function call contents in the group.
                    // Add the error message for each function call.
                    foreach (var functionCallContent in functionCallContentGroup)
                    {
                        // Add the function call content to the missing function chat message for DB storage.
                        errorFunctionMessage.Calls.Add(functionCallContent);

                        // Create the corresponding function result content with the error message.
                        var missingFunctionResultContent = new FunctionResultContent(
                            functionCallContent,
                            "Tool calling is disabled by the user");

                        // Add the function result content to the missing function chat message for DB storage.
                        errorFunctionMessage.Results.Add(missingFunctionResultContent);
                    }

                    errorFunctionMessage.ErrorMessageKey = new FormattedDynamicResourceKey(
                        LocaleKey.HandledFunctionInvokingException_FunctionCallingDisabled,
                        new DirectResourceKey(functionCallContentGroup.Key));

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
                        new DirectResourceKey(functionCallContentGroup.Key));
                    functionCallSpan.Add(errorFunctionMessage);

                    // Iterate through the function call contents in the group.
                    // Add the error message for each function call.
                    foreach (var functionCallContent in functionCallContentGroup)
                    {
                        // Add the function call content to the missing function chat message for DB storage.
                        errorFunctionMessage.Calls.Add(functionCallContent);

                        // Create the corresponding function result content with the error message.
                        var missingFunctionResultContent = new FunctionResultContent(functionCallContent, errorMessageBuilder.ToString());

                        // Add the function result content to the missing function chat message for DB storage.
                        errorFunctionMessage.Results.Add(missingFunctionResultContent);
                    }

                    errorFunctionMessage.ErrorMessageKey = new FormattedDynamicResourceKey(
                        LocaleKey.HandledFunctionInvokingException_FunctionNotFound,
                        new DirectResourceKey(functionCallContentGroup.Key));

                    continue;
                }

                var functionCallChatMessage = new FunctionCallChatMessage(
                    chatFunction.Icon ?? chatPlugin.Icon ?? LucideIconKind.Hammer,
                    chatFunction.HeaderKey)
                {
                    IsBusy = true,
                };
                functionCallSpan.Add(functionCallChatMessage);

                var functionCallContext = new FunctionCallContext(
                    kernel,
                    chatContext,
                    chatPlugin,
                    chatFunction,
                    functionCallChatMessage,
                    _settings.Plugin.IsPermissionGrantedRecords);
                chatContext.FunctionCallContext.Value = functionCallContext;

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

                        // Add the function call content to the function call chat message.
                        // This will record the function call in the database.
                        functionCallChatMessage.Calls.Add(functionCallContent);

                        // Also add a display block for the function call content.
                        // This will allow the UI to display the function call content.
                        var friendlyContent = chatFunction.GetFriendlyCallContent(functionCallContent);
                        if (friendlyContent is not null) functionCallChatMessage.DisplaySink.AppendBlock(friendlyContent);

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
                        functionCallChatMessage.Results.Add(resultContent);

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
                    chatContext.FunctionCallContext.Value = null;

                    if (cancellationToken.IsCancellationRequested)
                    {
                        functionCallChatMessage.ErrorMessageKey ??= new DynamicResourceKey(LocaleKey.FriendlyExceptionMessage_OperationCanceled);
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
            switch (consentDecision.Decision)
            {
                case ConsentDecision.AlwaysAllow:
                {
                    context.ChatFunction.AutoApprove = true;
                    break;
                }
                case ConsentDecision.AllowSession:
                {
                    context.ChatContext.IsPermissionGrantedRecords[permissionKey] = true;
                    break;
                }
                case ConsentDecision.Deny:
                {
                    return new FunctionResultContent(content, consentDecision.FormatReason("Tool execution denied by user."));
                }
            }

            resultContent = await content.InvokeAsync(context.Kernel, cancellationToken);
        }
        catch (Exception ex)
        {
            ex = HandledFunctionInvokingException.Handle(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Error invoking tool '{FunctionName}'", content.FunctionName);

            resultContent = new FunctionResultContent(content, $"Error: {ex.Message}") { InnerContent = ex };
        }

        return resultContent;

        Task<ConsentDecisionResult> ProcessConsentAsync(string permissionKey)
        {
            // Check if the permission is already granted in the current chat context
            if (!_settings.Plugin.IsPermissionGrantedRecords.TryGetValue(permissionKey, out var isPermissionGranted))
            {
                isPermissionGranted = context.IsPermissionGranted;
            }

            if (isPermissionGranted)
            {
                return Task.FromResult(ConsentDecisionResult.AllowOnce);
            }

            FormattedDynamicResourceKey headerKey;
            if (context.ChatPlugin is McpChatPlugin)
            {
                headerKey = new FormattedDynamicResourceKey(
                    LocaleKey.ChatPluginConsentRequest_MCP_Header,
                    context.ChatFunction.HeaderKey);
            }
            else
            {
                if (context.ChatFunction is BuiltInChatFunction { OnPermissionConsent: { } onPermissionConsent })
                {
                    return onPermissionConsent(content) switch
                    {
                        true => Task.FromResult(ConsentDecisionResult.AllowOnce),
                        false => Task.FromResult(ConsentDecisionResult.Deny()),
                        null => Task.FromResult(ConsentDecisionResult.AllowOnce) // Default to allow once
                    };
                }

                if (context.ChatFunction.Permissions == ChatFunctionPermissions.None)
                {
                    headerKey = new FormattedDynamicResourceKey(
                        LocaleKey.ChatPluginConsentRequest_CommonNone_Header,
                        context.ChatFunction.HeaderKey);
                }
                else
                {
                    headerKey = new FormattedDynamicResourceKey(
                        LocaleKey.ChatPluginConsentRequest_Common_Header,
                        context.ChatFunction.HeaderKey,
                        new DirectResourceKey(context.ChatFunction.Permissions.I18N(LocaleResolver.Common_Comma, true)));
                }
            }

            // The function requires permissions that are not granted.
            return context.ChatContext.HandleConsentRequestAsync(headerKey, displayBlock, RequestConsentRememberMasks.All, cancellationToken);
        }
    }

    private async Task GenerateTopicAsync(
        Assistant assistant,
        string userMessage,
        ChatContextMetadata metadata,
        CancellationToken cancellationToken)
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
                    Prompts.TitleGeneratorSystemPrompt),
                new ChatMessageContent(
                    AuthorRole.User,
                    ScopedPromptRenderer.RenderPrompt(
                        Prompts.TitleGeneratorUserPrompt,
                        key => key switch
                        {
                            "UserMessage" => userMessage.SafeSubstring(0, 2048),
                            "SystemLanguage" => language,
                            _ => null
                        })),
            };
            var usage = new ChatUsageDetails();
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
            ex = HandledChatException.Handle(ex, kernelMixin);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Failed to generate chat title");
        }
        finally
        {
            metadata.IsGeneratingTopic.FlipIfTrue();
        }
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

    // TODO: this is shit
    private sealed class ScopedPromptRenderer(
        IDictionary<string, Func<string>> promptVariables
    ) : IPromptRenderer
    {
        public static string RenderPrompt(string prompt, Func<string, string?> resolver) =>
            PromptTemplateRenderer.Render(prompt, resolver);

        public string RenderSystemPrompt(string prompt)
        {
            return RenderPrompt(prompt, key => promptVariables.TryGetValue(key, out var getter) ? getter() : null);
        }

        public string RenderStrategyUserPrompt(string strategyBody, string? userInput, PreprocessorResult? preprocessorResult)
        {
            var renderedStrategy = RenderPrompt(strategyBody, key =>
            {
                if (key == "Argument") return userInput ?? string.Empty;
                if (preprocessorResult?.Variables?.TryGetValue(key, out var val) == true) return val;
                if (promptVariables.TryGetValue(key, out var getter)) return getter();
                return null;
            });

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
