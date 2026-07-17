namespace Everywhere.Skills;

public enum SkillSourceRoot
{
    /// <summary>
    /// Built-in virtual skills.
    /// </summary>
    BuiltIn = -1,

    /// <summary>
    /// ~/.everywhere/skills
    /// </summary>
    Everywhere = 0,

    /// <summary>
    /// ~/.agents/skills
    /// </summary>
    Agents = 1,

    /// <summary>
    /// ~/.claude/skills
    /// </summary>
    Claude = 100000,

    /// <summary>
    /// ~/.codex/skills
    /// </summary>
    Codex = 200000,

    /// <summary>
    /// ~/.copilot/skills
    /// </summary>
    Copilot = 300000,

    /// <summary>
    /// ~/.cursor/skills
    /// </summary>
    Cursor = 400000,

    /// <summary>
    /// ~/.gemini/skills
    /// </summary>
    Gemini = 500000,
}