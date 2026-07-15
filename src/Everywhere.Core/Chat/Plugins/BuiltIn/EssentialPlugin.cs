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
/// e.g., run_subagent, manage_todo_list, etc.
/// </summary>
public sealed class EssentialPlugin : BuiltInChatPlugin
{
    public override IDynamicLocaleKey HeaderKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_Header);
    public override IDynamicLocaleKey DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_Description);
    public override LucideIconKind? Icon => LucideIconKind.ToolCase;
    public override bool IsDefaultEnabled => true;

    private readonly SystemAssistantSettings _systemAssistantSettings;
    private readonly ILogger<EssentialPlugin> _logger;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    private enum TodoAction
    {
        Reset,
        Read
    }

    public EssentialPlugin(Settings settings, ILogger<EssentialPlugin> logger) : base("essential")
    {
        _systemAssistantSettings = settings.SystemAssistant;
        _logger = logger;

        _functionsSource.Edit(list =>
        {
            list.Add(
                new BuiltInChatFunction(
                    RunSubagentAsync,
                    ChatFunctionPermissions.None));
            list.Add(
                new BuiltInChatFunction(
                    ManageTodoList,
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
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [FromKernelServices] ChatContext chatContext,
        [Description("A detailed description of the task for the agent to perform, inject into system prompt")] string prompt,
        [Description("A concise title for the agent's task")] string title,
        [Description("Optional, specifies the agent's area of expertise. Allowed values: default, image-understanding.")]
        string? specialization = null,
        CancellationToken cancellationToken = default)
    {
        displaySink.AppendDynamicLocaleKey(
            new FormattedDynamicLocaleKey(
                LocaleKey.BuiltInChatPlugin_Essential_RunSubagent_Title,
                new DirectLocaleKey(title)),
            "Large");

        // Fork a temporary chat context for the subagent
        var forkedChatContext = chatContext.ForkSubagent();
        forkedChatContext.Add(new UserChatMessage(prompt, []));
        var assistantChatMessage = new AssistantChatMessage();
        forkedChatContext.Add(assistantChatMessage);

        // Display the chat context in the UI
        displaySink.AppendSubagent(forkedChatContext);

        var specializations = specialization?.ToLower() switch
        {
            // ReSharper disable StringLiteralTypo
            "image-understanding" or "image_understanding" or "imageunderstanding" => ModelSpecializations.ImageUnderstanding,
            _ => ModelSpecializations.Default
        };
        var specializedAssistant = specializations switch
        {
            ModelSpecializations.ImageUnderstanding => _systemAssistantSettings.ImageUnderstanding.Resolve(assistant),
            _ => _systemAssistantSettings.DefaultSubagent.Resolve(assistant)
        };
        var systemPrompt = specializations switch
        {
            ModelSpecializations.ImageUnderstanding => DefaultPrompts.ImageUnderstandingSystemPrompt,
            _ => DefaultPrompts.DefaultSystemPrompt
        };

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

        var result = (assistantChatMessage.Items[^1] as AssistantChatMessageTextSpan)?.Content;
        return result ?? string.Empty;
    }

    [KernelFunction("manage_todo_list")]
    [Description(
        "Manage a structured todo list to track progress and plan tasks. " +
        "Use this tool to ensure task visibility and proper planning when dealing with complex or multi-step tasks.")]
    [DynamicLocaleKey(
        LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Header,
        LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Description)]
    private static string ManageTodoList(
        [FromKernelServices] ChatContext chatContext,
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        TodoAction action,
        [Description(
            "Complete array of all todo items (required for reset, optional for read). " +
            "ALWAYS provide complete list when resetting - partial updates not supported. " +
            "This MUST be a JSON array instead of a stringified JSON.")]
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
                chatContext.UserInterfaceBroker.SetTodoItems(items);
                displaySink.AppendDynamicLocaleKey(
                    new FormattedDynamicLocaleKey(
                        LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Reset,
                        new DirectLocaleKey(items.Count)));
                return "Todo list reset successfully";
            }
            case TodoAction.Read when chatContext.UserInterfaceBroker.TodoItems.Count is not > 0:
            {
                displaySink.AppendDynamicLocaleKey(
                    new FormattedDynamicLocaleKey(
                        LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Read,
                        new DirectLocaleKey(0)));

                return "Todo list is empty";
            }
            case TodoAction.Read:
            {
                displaySink.AppendDynamicLocaleKey(
                    new FormattedDynamicLocaleKey(
                        LocaleKey.BuiltInChatPlugin_Essential_ManageTodoList_Read,
                        new DirectLocaleKey(chatContext.UserInterfaceBroker.TodoItems.Count)));

                var sb = new StringBuilder();
                foreach (var item in chatContext.UserInterfaceBroker.TodoItems)
                {
                    sb.AppendLine($"- ID: {item.Id}, Status: {item.Status}, Title: {item.Title}");
                    if (!string.IsNullOrWhiteSpace(item.Description))
                    {
                        sb.AppendLine($"  Description: {item.Description}");
                    }
                }
                return sb.ToString();
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
