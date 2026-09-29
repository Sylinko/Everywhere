using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Chat.Permissions;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Statistics;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

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
    private readonly ILogger<EssentialPlugin> _logger;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    private enum TodoAction
    {
        Reset,
        Update,
        Read,
        Clear
    }

    public EssentialPlugin(
        Settings settings,
        AssistantSpecializationResolver assistantSpecializationResolver,
        ILogger<EssentialPlugin> logger
    ) : base("essential")
    {
        _systemAssistantSettings = settings.SystemAssistant;
        _assistantSpecializationResolver = assistantSpecializationResolver;
        _logger = logger;

        _functionsSource.Edit(list =>
        {
            list.Add(
                new BuiltInChatFunction(
                    RunSubagentAsync,
                    ChatFunctionPermissions.None));
            list.Add(
                new BuiltInChatFunction(
                    ManageTodo,
                    ChatFunctionPermissions.None));
            list.Add(
                new BuiltInChatFunction(
                    AskUserQuestionAsync,
                    ChatFunctionPermissions.None));
        });
    }

    [KernelFunction("run_subagent")]
    [Description(
        """
        Launch a new agent to handle complex tasks autonomously, which is good for complex tasks that require decision-making and planning.
        The agent can access tools as you can, except it CANNOT call run_subagent to avoid infinite recursion.
        After started, you will wait for the subagent to complete and return the final result as string.
        Each agent invocation is stateless and isolated, so make sure to provide all necessary context and instructions for the subagent.
        """)]
    [DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_RunSubagent_Header, LocaleKey.BuiltInChatPlugin_Essential_RunSubagent_Description)]
    private async Task<string> RunSubagentAsync(
        [FromKernelServices] IChatService chatService,
        [FromKernelServices] Assistant assistant,
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] ChatContext chatContext,
        [Description("A detailed description of the task for the agent to perform, inject into system prompt")]
        string prompt,
        [Description("A concise title for the agent's task")] string title,
        [Description("Optional, specifies the agent's area of expertise. Allowed values: default, image-understanding.")]
        string? specialization = null, // TODO: move image understanding to a separate function, and remove specialization parameter
        CancellationToken cancellationToken = default)
    {
        // Fork a temporary chat context for the subagent
        var forkedChatContext = chatContext.ForkSubagent(title);
        forkedChatContext.Add(new UserChatMessage(prompt, []));
        var assistantChatMessage = new AssistantChatMessage();
        forkedChatContext.Add(assistantChatMessage);

        // Display the chat context in the UI
        userInterface.ActivityPreview = new ChatPluginSubagentActivityPreview(forkedChatContext);
        userInterface.DisplaySink.AppendSubagent(forkedChatContext);

        var specializations = specialization?.ToLower() switch
        {
            // ReSharper disable StringLiteralTypo
            "image-understanding" or "image_understanding" or "imageunderstanding" => ModelSpecializations.ImageUnderstanding,
            _ => ModelSpecializations.Default
        };
        var specializedAssistant = specializations switch
        {
            ModelSpecializations.ImageUnderstanding => _assistantSpecializationResolver.Resolve(_systemAssistantSettings.ImageUnderstanding, assistant),
            _ => _assistantSpecializationResolver.Resolve(_systemAssistantSettings.DefaultSubagent, assistant)
        };
        var systemPrompt = specializations switch
        {
            ModelSpecializations.ImageUnderstanding => DefaultPrompts.ImageUnderstandingSystemPrompt,
            _ => DefaultPrompts.DefaultSystemPrompt
        };

        // The subagent has its own ChatContext and FunctionCallContext. Do not let the parent
        // tool's ambient invocation context leak into nested kernel-service resolution while the
        // child generation is waiting for model output or user consent.
        using var parentFunctionCallContextScope = chatContext.SuppressFunctionCallContext();
        await chatService.GenerateAsync(
            forkedChatContext,
            specializedAssistant,
            assistantChatMessage,
            systemPromptOverride: systemPrompt,
            enableNotifications: false,
            purpose: StatisticsModelInvocationPurpose.SubagentResponse,
            cancellationToken: cancellationToken);

        if (assistantChatMessage.Count < 1)
        {
            _logger.LogWarning("Subagent did not return any messages for task '{Title}'", title);
            return "The subagent did not return any response.";
        }

        // TODO: If subagent encounters an error, it should return a structured error message (with last available message) instead of a normal response.
        var result = (assistantChatMessage.Items[^1] as AssistantChatMessageTextSpan)?.Content;
        return result ?? string.Empty;
    }

    [KernelFunction("manage_todo")]
    [Description("Manage a temporary todo list for complex or multi-step tasks.")]
    [DynamicLocaleKey(
        LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Header,
        LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Description)]
    private static string ManageTodo(
        [FromKernelServices] ChatContext chatContext,
        [FromKernelServices] IChatPluginDisplaySink displaySink,
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

                chatContext.UserInterfaceBroker.TodoItems.SourceList.Reset(
                    items.Select(static item => item.Status is not null ?
                        item :
                        item with
                        {
                            Status = ChatPluginTodoStatus.NotStarted
                        }));
                displaySink.AppendDynamicLocaleKey(
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
                                new ArgumentException($"Todo item ID {item.Id} does not exist and cannot be added by update action.", nameof(items)));
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

                if (error != null)
                {
                    throw error;
                }

                displaySink.AppendDynamicLocaleKey(
                    new FormattedDynamicLocaleKey(
                        LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Update,
                        new DirectLocaleKey(items.Count)));

                return $"Updated: {string.Join(", ", items.Select(static item => $"#{item.Id}"))}.";
            }
            case TodoAction.Read:
            {
                var stringBuilder = new StringBuilder();
                chatContext.UserInterfaceBroker.TodoItems.SourceList.Edit(list =>
                {
                    displaySink.AppendDynamicLocaleKey(
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
                });

                return stringBuilder.TrimEnd().ToString();
            }
            case TodoAction.Clear:
            {
                chatContext.UserInterfaceBroker.TodoItems.SourceList.Clear();
                displaySink.AppendDynamicLocaleKey(new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Clear));
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

    [KernelFunction("ask_user_question")]
    [Description(
        "Use this tool to ask the user a small number of clarifying questions before proceeding. " +
        "Provide the questions array with concise headers and prompts. " +
        "Use options for fixed choices, set multiSelect when multiple selections are allowed.")]
    [DynamicLocaleKey(
        LocaleKey.BuiltInChatPlugin_Essential_AskUserQuestion_Header,
        LocaleKey.BuiltInChatPlugin_Essential_AskUserQuestion_Description)]
    private async static Task<IReadOnlyDictionary<string, ChatPluginQuestionAnswer>> AskUserQuestionAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
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

        userInterface.DisplaySink.AppendDynamicLocaleKey(
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
}