using Everywhere.StrategyEngine;

namespace Everywhere.AI;

public interface IPromptRenderer
{
    string RenderSystemPrompt(string prompt);

    string RenderStrategyUserPrompt(string strategyBody, string? userInput, PreprocessorResult? preprocessorResult);
}