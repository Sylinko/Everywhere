using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Harness;
using Everywhere.Statistics;
using Everywhere.Storage;
using Lucide.Avalonia;

namespace Everywhere.Chat.Plugins.BuiltIn;

/// <summary>
/// Provides essential functionalities for chat interactions.
/// e.g., run_subagent, manage_todo, etc.
/// </summary>
public sealed class EssentialPlugin : BuiltInChatPlugin
{
    public override IDynamicLocaleKey HeaderKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_Header);
    public override IDynamicLocaleKey DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_Description);
    public override LucideIconKind? Icon => LucideIconKind.ToolCase;
    public override bool IsDefaultEnabled => true;

    private readonly SystemAssistantSettings _systemAssistantSettings;
    private readonly AssistantSpecializationResolver _assistantSpecializationResolver;
    private readonly IBlobStorage _blobStorage;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    private enum TodoAction
    {
        Reset,
        Update,
        Read,
        Clear
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    private enum BackgroundJobAction
    {
        List,
        Output,
        Stop
    }

    public EssentialPlugin(
        Settings settings,
        AssistantSpecializationResolver assistantSpecializationResolver,
        IBlobStorage blobStorage
    ) : base("essential")
    {
        _systemAssistantSettings = settings.SystemAssistant;
        _assistantSpecializationResolver = assistantSpecializationResolver;
        _blobStorage = blobStorage;

        _functionsSource.Edit(list =>
        {
            list.Add(
                new BuiltInChatFunction(
                    RunSubagentAsync,
                    ChatFunctionPermissions.None,
                    supportsBackgrounding: true));
            list.Add(
                new BuiltInChatFunction(
                    ManageTodoAsync,
                    ChatFunctionPermissions.None));
            list.Add(
                new BuiltInChatFunction(
                    ManageBackgroundJobsAsync,
                    ChatFunctionPermissions.None,
                    icon: LucideIconKind.Layers,
                    isDefaultEnabled: true,
                    isVisible: false,
                    supportsParallelInvoke: true));
            list.Add(
                new BuiltInChatFunction(
                    AskUserQuestionAsync,
                    ChatFunctionPermissions.None));
            list.Add(
                new BuiltInChatFunction(
                    ViewImageAsync,
                    ChatFunctionPermissions.FileRead,
                    LucideIconKind.Image,
                    requiredModalities: Modalities.Image));
        });
    }

    [ChatFunction("run_subagent")]
    [Description(
        """
        Launch a new agent to handle complex tasks autonomously, which is good for complex tasks that require decision-making and planning.
        The agent can access tools as you can, except it CANNOT call run_subagent to avoid infinite recursion.
        After started, you will wait for the subagent to complete and return the final result as string.
        Each agent invocation is stateless and isolated, so make sure to provide all necessary context and instructions for the subagent.
        """)]
    [DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_RunSubagent_Header, LocaleKey.BuiltInChatPlugin_Essential_RunSubagent_Description)]
    private async Task<string> RunSubagentAsync(
        [FromChatServices] IChatService chatService,
        [FromChatServices] Assistant assistant,
        [FromChatServices] IChatPluginUserInterface userInterface,
        [FromChatServices] ChatContext chatContext,
        [Description("A detailed description of the task for the agent to perform, inject into system prompt")]
        string prompt,
        [Description("A concise title for the agent's task")] string title,
        [Description("Optional, specifies the agent's area of expertise. Allowed values: default, image-understanding.")]
        string? specialization = null, // TODO: move image understanding to a separate function, and remove specialization parameter
        CancellationToken cancellationToken = default)
    {
        // Fork a temporary chat context for the subagent
        var (nestedContext, userNode, assistantNode) = await chatContext.DispatchAsync(() =>
        {
            var child = chatContext.CreateNested(title);
            var user = child.Data.StartTurn(new UserChatMessage(prompt, []));
            var assistantMessage = new AssistantChatMessage();
            return (child, user, child.Data.Append(assistantMessage));
        });
        var forkedChatContext = nestedContext.Data;

        // Display the chat data in the UI
        await userInterface.SetActivityPreviewAsync(new ChatPluginSubagentActivityPreview(forkedChatContext));
        await userInterface.DisplaySink.AppendSubagentAsync(forkedChatContext);

        var specializations = specialization?.ToLowerInvariant() switch
        {
            // ReSharper disable StringLiteralTypo
            "image-understanding" or "image_understanding" or "imageunderstanding" => ModelSpecializations.ImageUnderstanding,
            _ => ModelSpecializations.Default
        };
        var specializedAssistant = specializations switch
        {
            ModelSpecializations.ImageUnderstanding => _assistantSpecializationResolver.Resolve(
                _systemAssistantSettings.ImageUnderstanding,
                assistant),
            _ => _assistantSpecializationResolver.Resolve(
                _systemAssistantSettings.DefaultSubagent,
                assistant)
        };
        var systemPrompt = specializations switch
        {
            ModelSpecializations.ImageUnderstanding => DefaultPrompts.ImageUnderstandingSystemPrompt,
            _ => DefaultPrompts.DefaultSystemPrompt
        };

        // The subagent has its own ChatData and FunctionCallContext. Do not let the parent
        // tool's ambient invocation context leak into nested kernel-service resolution while the
        // child generation is waiting for model output or user consent.
        using var parentFunctionCallContextScope = FunctionCallContext.SuppressCurrent(chatContext.GenerationContext);
        var generationResult = default(ChatGenerationResult);
        try
        {
            await nestedContext.ExecuteAsync(
                async childToken =>
                {
                    // Liveness starts only after work admission. GenerateAsync owns its completion.
                    await nestedContext.DispatchAsync(() => ((AssistantChatMessage)assistantNode.Message).IsBusy = true);
                    generationResult = await chatService.GenerateAsync(
                        nestedContext,
                        specializedAssistant,
                        assistantNode,
                        CompressionBoundary.Before(userNode),
                        systemPromptOverride: systemPrompt,
                        enableNotifications: false,
                        purpose: StatisticsModelInvocationPurpose.SubagentResponse,
                        cancellationToken: childToken);
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The subagent was stopped. Partial output: " +
                forkedChatContext.SelectedNodes.AsValueEnumerable().Select(node => node.Message).OfType<AssistantChatMessage>().LastOrDefault());
        }

        if (generationResult?.Error is { } error)
        {
            throw new HandledException(
                new Exception("The subagent failed: " + error.Message + ". Partial output: " + generationResult.Output, error),
                error.GetFriendlyMessage());
        }

        return generationResult?.Output ?? string.Empty;
    }

    [ChatFunction("manage_todo")]
    [Description("Manage a temporary todo list for complex or multi-step tasks.")]
    [DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Header, LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Description)]
    private static async Task<string> ManageTodoAsync(
        [FromChatServices] ChatContext chatContext,
        [FromChatServices] IChatPluginDisplaySink displaySink,
        [Description(
            """
            Use reset to replace the complete list, update to change existing items by ID, read to inspect the list, and clear to remove all items.
            Prefer update for progress changes so the complete list does not need to be sent again. Update never adds or removes items.
            """)]
        TodoAction action,
        [Description(
            """
            Todo items for reset or update; omit for read and clear.
            For reset, provide every item with id and title. For update, provide only existing IDs and fields to change; omitted fields stay unchanged.
            Use a JSON array, not a string containing JSON.
            """)]
        List<ChatPluginTodoItem>? items = null)
    {
        switch (action)
        {
            case TodoAction.Reset when items == null:
            {
                throw new HandledFunctionInvokingException(
                    HandledFunctionInvokingExceptionType.ArgumentMissing,
                    nameof(items),
                    new ArgumentException("items is required for reset action.", nameof(items)));
            }
            case TodoAction.Reset:
            {
                var ids = new HashSet<int>();
                foreach (var item in items)
                {
                    if (item.Title is null)
                    {
                        throw new HandledFunctionInvokingException(
                            HandledFunctionInvokingExceptionType.ArgumentError,
                            nameof(items),
                            new ArgumentException("Every item must have a title for reset action.", nameof(items)));
                    }

                    if (!ids.Add(item.Id))
                    {
                        throw new HandledFunctionInvokingException(
                            HandledFunctionInvokingExceptionType.ArgumentError,
                            nameof(items),
                            new ArgumentException($"Todo item ID {item.Id} is duplicated.", nameof(items)));
                    }
                }

                await chatContext.DispatchAsync(() =>
                {
                    chatContext.UserInterfaceBroker.TodoItems.SourceList.Reset(
                        items.Select(static item => item.Status is not null ?
                            item :
                            item with
                            {
                                Status = ChatPluginTodoStatus.NotStarted
                            }));
                });
                await displaySink.AppendDynamicLocaleKeyAsync(
                    new FormattedDynamicLocaleKey(
                        LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Reset,
                        new DirectLocaleKey(items.Count)));

                return $"Reset: {items.Count} item{(items.Count == 1 ? string.Empty : "s")}.";
            }
            case TodoAction.Update when items == null:
            {
                throw new HandledFunctionInvokingException(
                    HandledFunctionInvokingExceptionType.ArgumentMissing,
                    nameof(items),
                    new ArgumentException("items is required for update action.", nameof(items)));
            }
            case TodoAction.Update when items.Count == 0:
            {
                throw new HandledFunctionInvokingException(
                    HandledFunctionInvokingExceptionType.ArgumentError,
                    nameof(items),
                    new ArgumentException("At least one item must be provided for update action.", nameof(items)));
            }
            case TodoAction.Update:
            {
                HandledFunctionInvokingException? error = null;
                await chatContext.DispatchAsync(() =>
                {
                    chatContext.UserInterfaceBroker.TodoItems.SourceList.Edit(list =>
                    {
                        var ids = new HashSet<int>();
                        foreach (var item in items)
                        {
                            if (!ids.Add(item.Id))
                            {
                                error = new HandledFunctionInvokingException(
                                    HandledFunctionInvokingExceptionType.ArgumentError,
                                    nameof(items),
                                    new ArgumentException($"Todo item ID {item.Id} is duplicated in update action.", nameof(items)));
                                return;
                            }

                            if (list.AsValueEnumerable().All(i => i.Id != item.Id))
                            {
                                error = new HandledFunctionInvokingException(
                                    HandledFunctionInvokingExceptionType.ArgumentError,
                                    nameof(items),
                                    new ArgumentException(
                                        $"Todo item ID {item.Id} does not exist and cannot be added by update action.",
                                        nameof(items)));
                                return;
                            }
                        }

                        foreach (var item in items)
                        {
                            for (var index = 0; index < list.Count; index++)
                            {
                                if (list[index].Id != item.Id) continue;

                                var currentItem = list[index];
                                list[index] = currentItem with
                                {
                                    Title = item.Title ?? currentItem.Title,
                                    Description = item.Description ?? currentItem.Description,
                                    Status = item.Status ?? currentItem.Status
                                };
                                break;
                            }
                        }
                    });
                });

                if (error != null)
                {
                    throw error;
                }

                await displaySink.AppendDynamicLocaleKeyAsync(
                    new FormattedDynamicLocaleKey(
                        LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Update,
                        new DirectLocaleKey(items.Count)));

                return $"Updated: {string.Join(", ", items.Select(static item => $"#{item.Id}"))}.";
            }
            case TodoAction.Read:
            {
                var stringBuilder = new StringBuilder();
                var list = chatContext.UserInterfaceBroker.TodoItems.SourceList.Items;
                await displaySink.AppendDynamicLocaleKeyAsync(
                    new FormattedDynamicLocaleKey(
                        LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Read,
                        new DirectLocaleKey(list.Count)));

                stringBuilder.Append("Todo (").Append(list.Count).AppendLine(")");

                foreach (var item in list)
                {
                    stringBuilder
                        .Append("- ")
                        .Append(
                            item.Status switch
                            {
                                null or ChatPluginTodoStatus.NotStarted => "[ ]",
                                ChatPluginTodoStatus.InProgress => "[~]",
                                ChatPluginTodoStatus.Completed => "[x]",
                                _ => "[?]"
                            })
                        .Append(" #")
                        .Append(item.Id)
                        .Append(' ')
                        .AppendLine((item.Title ?? string.Empty).ReplaceLineEndings(" ").Trim());

                    if (!string.IsNullOrWhiteSpace(item.Description))
                    {
                        foreach (var line in item.Description.Trim().ReplaceLineEndings("\n").Split('\n'))
                        {
                            stringBuilder.Append("  > ").AppendLine(line);
                        }
                    }
                }

                return stringBuilder.TrimEnd().ToString();
            }
            case TodoAction.Clear:
            {
                await chatContext.DispatchAsync(() => chatContext.UserInterfaceBroker.TodoItems.SourceList.Clear());
                await displaySink.AppendDynamicLocaleKeyAsync(new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Clear));
                return "Cleared todo list.";
            }
            default:
            {
                throw new HandledFunctionInvokingException(
                    HandledFunctionInvokingExceptionType.ArgumentError,
                    nameof(action),
                    new ArgumentException("Invalid action.", nameof(action)));
            }
        }
    }

    [ChatFunction("ask_user_question")]
    [Description(
        "Use this tool to ask the user a small number of clarifying questions before proceeding. " +
        "Provide the questions array with concise headers and prompts. " +
        "Use options for fixed choices, set multiSelect when multiple selections are allowed.")]
    [DynamicLocaleKey(
        LocaleKey.BuiltInChatPlugin_Essential_AskUserQuestion_Header,
        LocaleKey.BuiltInChatPlugin_Essential_AskUserQuestion_Description)]
    private async static Task<IReadOnlyDictionary<string, ChatPluginQuestionAnswer>> AskUserQuestionAsync(
        [FromChatServices] IChatPluginUserInterface userInterface,
        [Description("The questions to present to the user. Each question is shown as a separate page.")]
        IReadOnlyList<ChatPluginQuestion> questions,
        CancellationToken cancellationToken)
    {
        if (questions.Count == 0)
        {
            throw new HandledFunctionInvokingException(
                HandledFunctionInvokingExceptionType.ArgumentError,
                nameof(questions),
                new ArgumentException("At least one question must be provided.", nameof(questions)));
        }

        await userInterface.DisplaySink.AppendDynamicLocaleKeyAsync(
            new FormattedDynamicLocaleKey(
                LocaleKey.BuiltInChatPlugin_Essential_AskUserQuestion_Prompt,
                new DirectLocaleKey(questions.Count)));

        var answers = await userInterface.AskQuestionAsync(questions, cancellationToken);
        if (answers.Count != questions.Count)
        {
            throw new HandledFunctionInvokingException(
                HandledFunctionInvokingExceptionType.InvalidResult,
                "The number of answers does not match the number of questions.");
        }

        var result = new Dictionary<string, ChatPluginQuestionAnswer>(answers.Count);
        for (var i = 0; i < answers.Count; i++)
        {
            var question = questions[i];
            var answer = answers[i];
            result[question.Id] = answer;
        }

        return result;
    }

    [ChatFunction("view_image")]
    [Description(
        """
        View still images from local files, including image attachments. Pass up to 8 paths in the order you want to compare them.
        Large images are resized automatically. If small details are unreadable, crop the relevant region into a separate file and view that file.
        """)]
    [DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_ViewImage_Header, LocaleKey.BuiltInChatPlugin_Essential_ViewImage_Description)]
    private async Task<ViewImageFunctionResult> ViewImageAsync(
        [FromChatServices] ChatContext chatContext,
        [Description("Local paths or file:// URIs. Relative paths resolve against the working directory.")]
        string[] paths,
        CancellationToken cancellationToken)
    {
        if (paths is null || paths.Length is < 1 or > 8)
        {
            throw new HandledException(
                new ArgumentException("Provide between 1 and 8 image paths.", nameof(paths)),
                LocaleKey.BuiltInChatPlugin_Essential_ViewImage_PathCount);
        }

        var items = new List<ViewImageItem>(paths.Length);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                items.Add(ViewImageItem.EmptyPath(path));
                continue;
            }

            var localPath = path;
            try
            {
                localPath = LocalFileHandler.ExpandLocalPath(localPath, chatContext.WorkingDirectory);
                var image = ChatImage.Read(localPath);
                using var stream = new MemoryStream(image.Bytes, writable: false);
                var blob = await _blobStorage.StorageBlobAsync(stream, image.MimeType, cancellationToken: cancellationToken);
                var preview = new FileAttachment(
                    new DirectLocaleKey(Path.GetFileName(localPath)),
                    blob.LocalPath,
                    blob.Sha256,
                    image.MimeType);
                items.Add(ViewImageItem.FromPreparedImage(localPath, preview, image));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or HandledException)
            {
                items.Add(ViewImageItem.FromError(localPath, error));
            }
        }

        return new ViewImageFunctionResult(items);
    }

    [ChatFunction("manage_background_jobs")]
    [Description(
        """
        List, inspect live output from, or stop background jobs started during this chat session.
        Use list to inspect active and completed background jobs; use output with jobId to read live output; use stop with jobId to cancel a running job.
        """)]
    [DynamicLocaleKey(
        LocaleKey.BuiltInChatPlugin_Essential_ManageBackgroundJobs_Header,
        LocaleKey.BuiltInChatPlugin_Essential_ManageBackgroundJobs_Description)]
    private static string ManageBackgroundJobsAsync(
        [FromChatServices] ChatContext chatContext,
        [Description("Action to perform: list, output, or stop.")]
        BackgroundJobAction action,
        [Description("Target background job ID (required for output and stop actions).")]
        string? jobId = null)
    {
        var manager = chatContext.Root.BackgroundJobs;
        switch (action)
        {
            case BackgroundJobAction.List:
            {
                var jobs = manager.ListJobs();
                if (jobs.Count == 0)
                {
                    return "No background jobs have been started in this session.";
                }

                var builder = new StringBuilder();
                foreach (var job in jobs)
                {
                    var status = !job.IsCompleted ? "running" : job.IsSuccess ? "completed" : "failed";
                    builder
                        .Append(job.JobId)
                        .Append(" | ")
                        .Append(status)
                        .Append(" | ")
                        .Append(job.ToolName)
                        .Append(" | ")
                        .AppendLine(job.Label);
                }

                return builder.TrimEnd().ToString();
            }
            case BackgroundJobAction.Output:
            {
                if (string.IsNullOrWhiteSpace(jobId))
                {
                    throw new HandledFunctionInvokingException(
                        HandledFunctionInvokingExceptionType.ArgumentMissing,
                        nameof(jobId),
                        new ArgumentException("jobId is required for output action.", nameof(jobId)));
                }

                if (!manager.TryGetJob(jobId.Trim(), out var job))
                {
                    return $"Background job '{jobId}' was not found.";
                }

                var status = !job.IsCompleted ? "running" : job.IsSuccess ? "completed" : "failed";
                var liveOutput = job.ReadLiveOutput();
                var builder = new StringBuilder();
                builder.AppendLine($"Job: {job.JobId} ({job.ToolName}) [{status}] - {job.Label}");
                if (!string.IsNullOrWhiteSpace(liveOutput))
                {
                    builder.AppendLine(liveOutput);
                }
                else if (!job.IsCompleted)
                {
                    builder.AppendLine("(no output yet)");
                }

                return builder.TrimEnd().ToString();
            }
            case BackgroundJobAction.Stop:
            {
                if (string.IsNullOrWhiteSpace(jobId))
                {
                    throw new HandledFunctionInvokingException(
                        HandledFunctionInvokingExceptionType.ArgumentMissing,
                        nameof(jobId),
                        new ArgumentException("jobId is required for stop action.", nameof(jobId)));
                }

                var normalizedId = jobId.Trim();
                if (!manager.TryGetJob(normalizedId, out var job))
                {
                    return $"Background job '{jobId}' was not found.";
                }

                return manager.TryStopJob(normalizedId) ?
                    $"Stop requested for background job '{job.JobId}' ({job.Label})." :
                    $"Background job '{job.JobId}' is already completed.";
            }
            default:
            {
                throw new HandledFunctionInvokingException(
                    HandledFunctionInvokingExceptionType.ArgumentError,
                    nameof(action),
                    new ArgumentOutOfRangeException(nameof(action), action, "Invalid background job action."));
            }
        }
    }
}