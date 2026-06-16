using Everywhere.StrategyEngine.Conditions;
using Lucide.Avalonia;

namespace Everywhere.StrategyEngine.BuiltIn;

/// <summary>
/// Strategy for code editor contexts.
/// Provides commands for code review, explanation, and refactoring.
/// </summary>
public sealed class CodeEditorStrategyProvcider : BuiltInStrategyProvider
{
    private IStrategyCondition Condition { get; } =
        CompositeCondition.Or(
            new VisualElementCondition
            {
                ProcessNames =
                [
                    "code", "Visual Studio Code",
                    "cursor",
                    "devenv", "Visual Studio",
                    // "idea", "idea64", "IntelliJ IDEA",
                    // "pycharm", "pycharm64", "PyCharm",
                    // "webstorm", "webstorm64", "WebStorm",
                    // "rider", "rider64", "Rider",
                    // "clion", "clion64", "CLion",
                    // "goland", "goland64", "GoLand",
                    // "rustrover", "RustRover",
                    "sublime_text", "Sublime Text",
                    "atom",
                    "nvim", "vim",
                ],
                MinCount = 1
            },
            new FileCondition
            {
                Extensions =
                [
                    ".py", ".js", ".ts", ".jsx", ".tsx",
                    ".cs", ".java", ".kt", ".go", ".rs",
                    ".cpp", ".c", ".h", ".hpp",
                    ".rb", ".php", ".swift", ".scala",
                    ".vue", ".svelte", ".astro",
                    ".json", ".yaml", ".yml", ".toml",
                    ".html", ".css", ".scss", ".less",
                    ".sql", ".sh", ".bash", ".zsh", ".ps1",
                    ".md", ".markdown"
                ],
                MinCount = 1
            }
        );

    public override IEnumerable<Strategy> GetStrategies() =>
    [
        // Explain code
        new()
        {
            Id = "explain",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_CodeEditor_ExplainCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_CodeEditor_ExplainCommand_Description),
            Icon = LucideIconKind.MessageSquareCode,
            Priority = 100,
            Condition = Condition,
            Body =
                """
                You are an expert programmer and code educator.
                Explain the provided code clearly and thoroughly:
                - What does it do overall?
                - How does it work step by step?
                - What are the key concepts used?
                - Are there any notable patterns or techniques?
                Adjust your explanation to the complexity of the code.
                """
        },

        // Review code
        new()
        {
            Id = "review",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_CodeEditor_ReviewCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_CodeEditor_ReviewCommand_Description),
            Icon = LucideIconKind.SearchCode,
            Priority = 90,
            Condition = Condition,
            Body =
                """
                You are a senior software engineer conducting a code review.
                Review the provided code and provide constructive feedback on:
                - Code quality and readability
                - Potential bugs or issues
                - Performance considerations
                - Best practices and conventions
                - Suggestions for improvement
                Be specific and provide examples where helpful.
                """
        },

        // Add documentation
        new()
        {
            Id = "document",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_CodeEditor_DocumentCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_CodeEditor_DocumentCommand_Description),
            Icon = LucideIconKind.FileCode,
            Priority = 70,
            Condition = Condition,
            Body =
                """
                You are a technical documentation specialist.
                Generate appropriate documentation for the provided code:
                - Add doc comments (JSDoc, docstrings, XML docs, etc.)
                - Document parameters, return values, and exceptions
                - Explain complex logic with inline comments
                - Follow the conventions of the programming language
                Return the code with documentation added.
                """
        },

        // Find bugs
        new()
        {
            Id = "find-bugs",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_CodeEditor_FindBugsCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_CodeEditor_FindBugsCommand_Description),
            Icon = LucideIconKind.Bug,
            Priority = 85,
            Condition = Condition,
            Body =
                """
                You are a bug-hunting expert and static analysis specialist.
                Analyze the provided code for potential bugs and issues:
                - Logic errors
                - Edge cases not handled
                - Null/undefined issues
                - Resource leaks
                - Race conditions or concurrency issues
                - Security vulnerabilities
                For each issue found, explain the problem and suggest a fix.
                """
        },

        // Optimize performance
        new()
        {
            Id = "optimize",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_CodeEditor_OptimizeCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_CodeEditor_OptimizeCommand_Description),
            Icon = LucideIconKind.Zap,
            Priority = 60,
            Condition = Condition,
            Body =
                """
                You are a performance optimization expert.
                Analyze the provided code for performance issues:
                - Identify inefficient algorithms or data structures
                - Find unnecessary operations or redundant code
                - Suggest caching or memoization opportunities
                - Consider memory usage and allocation
                Provide optimized code with explanations of the improvements.
                """
        },
    ];
}