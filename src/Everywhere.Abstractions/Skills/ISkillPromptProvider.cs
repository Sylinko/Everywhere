using Everywhere.AI;

namespace Everywhere.Skills;

public interface ISkillPromptProvider
{
    string GetPrompt(ToolCallStatus toolCallStatus);
}