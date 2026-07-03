using ZLinq;
using Lucide.Avalonia;

namespace Everywhere.AI.Prompts;

/// <summary>
/// Built-in deterministic choices for the prompt guided creator.
/// </summary>
/// <remarks>
/// Recipes are an authoring aid, not a runtime format. The saved prompt is still ordinary template
/// text; the snapshot only lets UI explain where it came from and provide display-name fallback.
/// </remarks>
public static class PromptRecipeCatalog
{
    public const int CurrentSnapshotSchemaVersion = 1;
    public const int MaximumScenarioCount = 3;

    public static IReadOnlyList<PromptRecipeOption> Personas { get; } =
    [
        new("general", LocaleKey.PromptRecipe_Persona_General, "You are a helpful, careful, and pleasant general assistant."),
        new(
            "professional",
            LocaleKey.PromptRecipe_Persona_ProfessionalAdvisor,
            "You are a professional advisor who gives practical, risk-aware recommendations."),
        new(
            "programming",
            LocaleKey.PromptRecipe_Persona_ProgrammingAssistant,
            "You are a programming assistant who explains tradeoffs and writes maintainable code."),
        new("writing", LocaleKey.PromptRecipe_Persona_WritingEditor, "You are a writing editor who improves clarity, structure, and voice."),
        new(
            "translation",
            LocaleKey.PromptRecipe_Persona_TranslationAssistant,
            "You are a translation assistant who preserves meaning, tone, and context."),
        new(
            "research",
            LocaleKey.PromptRecipe_Persona_ResearchAssistant,
            "You are a research assistant who investigates carefully and separates evidence from inference."),
        new(
            "learning",
            LocaleKey.PromptRecipe_Persona_LearningMentor,
            "You are a learning mentor who explains concepts patiently and checks understanding."),
        new(
            "product",
            LocaleKey.PromptRecipe_Persona_ProductStrategyPartner,
            "You are a product and strategy partner who balances user needs, execution, and business context.")
    ];

    public static IReadOnlyList<PromptRecipeOption> Scenarios { get; } =
    [
        new("general-qa", LocaleKey.PromptRecipe_Scenario_GeneralQA, "General Q&A", LucideIconKind.MessageCircleQuestionMark),
        new("programming-development", LocaleKey.PromptRecipe_Scenario_ProgrammingDevelopment, "Programming and development", LucideIconKind.Code),
        new("code-review", LocaleKey.PromptRecipe_Scenario_CodeReview, "Code review", LucideIconKind.FileCode),
        new("writing-editing", LocaleKey.PromptRecipe_Scenario_WritingEditing, "Writing and editing", LucideIconKind.PenLine),
        new("data-analysis", LocaleKey.PromptRecipe_Scenario_DataAnalysis, "Data analysis", LucideIconKind.ChartBar),
        new("learning-education", LocaleKey.PromptRecipe_Scenario_LearningEducation, "Learning and education", LucideIconKind.GraduationCap),
        new("translation-language", LocaleKey.PromptRecipe_Scenario_TranslationLanguage, "Translation and language", LucideIconKind.Languages),
        new("product-planning", LocaleKey.PromptRecipe_Scenario_ProductPlanning, "Product planning", LucideIconKind.Lightbulb),
        new("troubleshooting", LocaleKey.PromptRecipe_Scenario_Troubleshooting, "Troubleshooting", LucideIconKind.Bug),
        new("research-planning", LocaleKey.PromptRecipe_Scenario_ResearchPlanning, "Research and planning", LucideIconKind.Search)
    ];

    public static IReadOnlyList<PromptRecipeOption> Tones { get; } =
    [
        new("professional-rigorous", LocaleKey.PromptRecipe_Tone_ProfessionalRigorous, "Be professional and rigorous.", LucideIconKind.ShieldCheck),
        new("friendly-patient", LocaleKey.PromptRecipe_Tone_FriendlyPatient, "Be friendly, patient, and encouraging.", LucideIconKind.Heart),
        new("concise-direct", LocaleKey.PromptRecipe_Tone_ConciseDirect, "Be concise and direct.", LucideIconKind.ListChecks),
        new(
            "socratic-guiding",
            LocaleKey.PromptRecipe_Tone_SocraticGuiding,
            "Guide the user with questions when that helps them think.",
            LucideIconKind.MessageCircleQuestionMark),
        new("formal-business", LocaleKey.PromptRecipe_Tone_FormalBusiness, "Use a formal business style.", LucideIconKind.Briefcase),
        new(
            "creative-exploratory",
            LocaleKey.PromptRecipe_Tone_CreativeExploratory,
            "Be creative, exploratory, and open to alternatives.",
            LucideIconKind.Sparkles)
    ];

    public static IReadOnlyList<PromptRecipeOption> DetailLevels { get; } =
    [
        new("concise", LocaleKey.PromptRecipe_Detail_Concise, "Use a concise level of detail.", LucideIconKind.TextInitial),
        new("balanced", LocaleKey.PromptRecipe_Detail_Balanced, "Use a balanced level of detail.", LucideIconKind.List),
        new("detailed", LocaleKey.PromptRecipe_Detail_Detailed, "Use detailed explanations when they are useful.", LucideIconKind.FileText)
    ];

    public static IReadOnlyList<PromptRecipeOption> Organizations { get; } =
    [
        new(
            "automatic",
            LocaleKey.PromptRecipe_Organization_Automatic,
            "Choose the clearest structure automatically. Use paragraphs, lists, steps, or tables when they improve readability.",
            LucideIconKind.Settings2),
        new(
            "conclusion-first",
            LocaleKey.PromptRecipe_Organization_ConclusionFirst,
            "Start with the conclusion, then provide supporting details.",
            LucideIconKind.ChevronsRight),
        new(
            "step-by-step",
            LocaleKey.PromptRecipe_Organization_StepByStep,
            "Use step-by-step explanations when the task has a process.",
            LucideIconKind.ListOrdered),
        new(
            "key-points",
            LocaleKey.PromptRecipe_Organization_KeyPoints,
            "Summarize with key points before adding details.",
            LucideIconKind.ListChecks),
        new(
            "compare-options",
            LocaleKey.PromptRecipe_Organization_CompareOptions,
            "Compare options clearly, including tradeoffs and recommendations.",
            LucideIconKind.GitCompare),
        new(
            "examples-first",
            LocaleKey.PromptRecipe_Organization_ExamplesFirst,
            "Lead with concrete examples when they make the answer easier to understand.",
            LucideIconKind.FileText),
        new(
            "natural-conversation",
            LocaleKey.PromptRecipe_Organization_NaturalConversation,
            "Reply in a natural conversational structure unless the user asks for a stricter format.",
            LucideIconKind.MessageCircle)
    ];

    public static PromptRecipeSnapshot CreateDefaultSnapshot() => new()
    {
        SchemaVersion = CurrentSnapshotSchemaVersion,
        PersonaId = Personas[0].Id,
        ScenarioIds = [Scenarios[0].Id],
        ToneId = Tones[0].Id,
        DetailLevelId = DetailLevels[1].Id,
        OrganizationId = Organizations[0].Id
    };

    public static PromptRecipeSnapshot NormalizeSnapshot(PromptRecipeSnapshot snapshot, bool detached)
    {
        var fallback = CreateDefaultSnapshot();
        return new PromptRecipeSnapshot
        {
            SchemaVersion = CurrentSnapshotSchemaVersion,
            PersonaId = FindOption(Personas, snapshot.PersonaId)?.Id ?? fallback.PersonaId,
            PreferredUserName = NormalizeOptionalText(snapshot.PreferredUserName),
            ScenarioIds =
            [
                .. snapshot.ScenarioIds
                    .AsValueEnumerable()
                    .Select(id => FindOption(Scenarios, id)?.Id)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Take(MaximumScenarioCount)
            ],
            ToneId = FindOption(Tones, snapshot.ToneId)?.Id ?? fallback.ToneId,
            DetailLevelId = FindOption(DetailLevels, snapshot.DetailLevelId)?.Id ?? fallback.DetailLevelId,
            OrganizationId = FindOption(Organizations, snapshot.OrganizationId)?.Id ?? fallback.OrganizationId,
            AdditionalRequirements = NormalizeOptionalText(snapshot.AdditionalRequirements),
            IsDetachedFromRecipe = detached
        };
    }

    public static string ComposeTemplate(PromptRecipeSnapshot snapshot)
    {
        var normalized = NormalizeSnapshot(snapshot, snapshot.IsDetachedFromRecipe);
        var persona = FindOption(Personas, normalized.PersonaId).NotNull();
        var tone = FindOption(Tones, normalized.ToneId).NotNull();
        var detail = FindOption(DetailLevels, normalized.DetailLevelId).NotNull();
        var organization = FindOption(Organizations, normalized.OrganizationId).NotNull();
        var scenarios = normalized.ScenarioIds
            .AsValueEnumerable()
            .Select(id => FindOption(Scenarios, id))
            .OfType<PromptRecipeOption>()
            .ToList();

        var sections = new List<string>
        {
            "{" + SystemPromptPlaceholderSource.DefaultSystemPromptName + "}"
        };

        if (!string.IsNullOrWhiteSpace(normalized.PreferredUserName))
        {
            sections.Add(
                "# User Preference\n" +
                $"Address the user as: {normalized.PreferredUserName}");
        }

        sections.Add("# Persona\n" + persona.TemplateFragment);

        if (scenarios.Count > 0)
        {
            sections.Add(
                "# Work Scenarios\n" +
                "This prompt is optimized for:\n" +
                string.Join("\n", scenarios.Select(static scenario => "- " + scenario.TemplateFragment)) +
                "\n\nWhen scenario guidance conflicts, prioritize the user's current request.");
        }

        sections.Add("# Interaction Style\n" + tone.TemplateFragment);
        sections.Add("# Detail Level\n" + detail.TemplateFragment);
        sections.Add("# Organization\n" + organization.TemplateFragment);

        if (!string.IsNullOrWhiteSpace(normalized.AdditionalRequirements))
        {
            sections.Add("# Additional Requirements\n" + normalized.AdditionalRequirements);
        }

        return string.Join("\n\n", sections);
    }

    public static IDynamicLocaleKey? GetPersonaNameKey(string? personaId) =>
        FindOption(Personas, personaId)?.NameKey;

    public static PromptRecipeOption? FindOption(IReadOnlyList<PromptRecipeOption> options, string? id) =>
        string.IsNullOrWhiteSpace(id) ?
            null :
            options.AsValueEnumerable().FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.Ordinal));

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// One deterministic choice used by guided prompt creation.
/// </summary>
/// <param name="TemplateFragment">
/// English prompt fragment written into the generated template. UI localization is intentionally
/// separate from template generation so changing app language does not rewrite saved prompts.
/// </param>
public sealed record PromptRecipeOption(
    string Id,
    IDynamicLocaleKey NameKey,
    string TemplateFragment,
    LucideIconKind? Icon = null
)
{
    public PromptRecipeOption(string id, string nameKey, string templateFragment, LucideIconKind? icon = null)
        : this(id, new DynamicLocaleKey(nameKey), templateFragment, icon)
    {
    }
}