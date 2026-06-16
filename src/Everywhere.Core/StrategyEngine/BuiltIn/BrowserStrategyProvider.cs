using Everywhere.StrategyEngine.Conditions;
using Lucide.Avalonia;

namespace Everywhere.StrategyEngine.BuiltIn;

/// <summary>
/// Strategy for browser-related contexts.
/// Provides commands for web pages, articles, and online content.
/// </summary>
public sealed class BrowserStrategyProvider : BuiltInStrategyProvider
{
    // Common browser process names across platforms
    private static readonly string[] BrowserProcessNames =
    [
        "chrome", "Google Chrome",
        "firefox",
        "msedge", "Microsoft Edge",
        "safari",
        "opera",
        "brave", "Brave Browser",
        "vivaldi",
        "arc",
    ];

    private static IStrategyCondition Condition { get; } =
        new VisualElementCondition
        {
            ProcessNames = BrowserProcessNames,
            MinCount = 1
        };

    public override IEnumerable<Strategy> GetStrategies() =>
    [
        // Translate page
        new()
        {
            Id = "translate",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_Browser_TranslateCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_Browser_TranslateCommand_Description),
            Icon = LucideIconKind.Languages,
            Priority = 100,
            Condition = Condition,
            Body =
                """
                You are a professional translator.
                Translate the provided web page content to the user's preferred language.
                Maintain the original formatting and structure where possible.
                If the content is already in the target language, inform the user.
                """,
            ArgumentHintKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_Browser_TranslateCommand_ArgumentHint)
        },

        // Summarize page
        new()
        {
            Id = "summarize",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_Browser_SummarizePageCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_Browser_SummarizePageCommand_Description),
            Icon = LucideIconKind.FileText,
            Priority = 90,
            Condition = Condition,
            Body =
                """
                You are an expert at summarizing web content.
                Provide a clear, structured summary of the web page including:
                - Main topic/purpose
                - Key points or findings
                - Important details or conclusions
                Keep the summary concise but comprehensive.
                """
        },

        // Extract key information
        new()
        {
            Id = "extract-info",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_Browser_ExtractInfoCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_Browser_ExtractInfoCommand_Description),
            Icon = LucideIconKind.ListChecks,
            Priority = 80,
            Condition = Condition,
            Body =
                """
                You are an information extraction specialist.
                Extract and organize the key information from this web page.
                Present the information in a clear, structured format.
                Include: names, dates, numbers, facts, and any actionable items.
                """
        },

        // Explain technical content
        new()
        {
            Id = "explain",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_Browser_ExplainCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_Browser_ExplainCommand_Description),
            Icon = LucideIconKind.GraduationCap,
            Priority = 70,
            Condition = Condition,
            Body =
                """
                You are an expert educator.
                Explain the content on this page in simple, easy-to-understand terms.
                Break down complex concepts into digestible pieces.
                Use analogies and examples where helpful.
                Adjust your explanation to the apparent complexity of the content.
                """
        },

        // Fact check
        new()
        {
            Id = "fact-check",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_Browser_FactCheckCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_Browser_FactCheckCommand_Description),
            Icon = LucideIconKind.ShieldCheck,
            Priority = 60,
            Condition = Condition,
            ToolRulesets = new(1)
            {
                { "builtin.web_browser.*", true }
            },
            Body =
                """
                You are a fact-checking specialist.
                Review the claims made on this page and assess their accuracy.
                For each major claim:
                - State the claim
                - Assess its accuracy (true, false, partially true, unverifiable)
                - Provide context or corrections if needed
                Be objective and cite sources when possible.
                """
        }
    ];
}