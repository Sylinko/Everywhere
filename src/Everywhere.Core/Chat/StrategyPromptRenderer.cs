using System.Text;
using Everywhere.StrategyEngine;

namespace Everywhere.Chat;

internal static class StrategyPromptRenderer
{
    public static string RenderSystemPrompt(
        string prompt,
        IDictionary<string, Func<string>> promptVariables,
        PreprocessorResult? preprocessorResult) =>
        PromptTemplateRenderer.Render(prompt, key => Resolve(key, promptVariables, preprocessorResult, null));

    public static string RenderUserPrompt(
        string strategyBody,
        string? userInput,
        PreprocessorResult? preprocessorResult,
        IDictionary<string, Func<string>> promptVariables)
    {
        var renderedStrategy = PromptTemplateRenderer.Render(
            strategyBody,
            key => Resolve(key, promptVariables, preprocessorResult, userInput));

        if (string.IsNullOrEmpty(userInput) || strategyBody.Contains("{Argument}", StringComparison.Ordinal))
        {
            return renderedStrategy;
        }

        return new StringBuilder(renderedStrategy)
            .AppendLine()
            .AppendLine("<UserRequestStart>")
            .Append(userInput)
            .ToString();
    }

    private static string? Resolve(
        string key,
        IDictionary<string, Func<string>> promptVariables,
        PreprocessorResult? preprocessorResult,
        string? userInput)
    {
        if (preprocessorResult?.Variables?.TryGetValue(key, out var value) == true)
        {
            return value;
        }

        if (key == "Argument")
        {
            return userInput ?? string.Empty;
        }

        return promptVariables.TryGetValue(key, out var getter) ? getter() : null;
    }
}
