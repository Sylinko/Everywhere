using Everywhere.StrategyEngine;

namespace Everywhere.AI;

public interface IPromptRenderer
{
    string RenderSystemPrompt(string prompt, PreprocessorResult? preprocessorResult = null);

    string RenderStrategyUserPrompt(string strategyBody, string? userInput, PreprocessorResult? preprocessorResult);
}