namespace Everywhere.AI.Prompts;

/// <summary>
/// Holds the localized user question and assistant reply keys illustrating a guided prompt recipe.
/// </summary>
public readonly record struct PromptRecipePreview(IDynamicLocaleKey QuestionKey, IDynamicLocaleKey ReplyKey);

/// <summary>
/// Builds live example conversation previews from guided prompt recipe selections.
/// </summary>
public static class PromptRecipePreviewBuilder
{
    /// <summary>
    /// Creates a preview question and reply pair reflecting the supplied recipe snapshot and primary scenario.
    /// </summary>
    public static PromptRecipePreview Build(PromptRecipeSnapshot snapshot, string? primaryScenarioId)
    {
        var questionKeyName = primaryScenarioId switch
        {
            "programming-development" => LocaleKey.Oobe_Preview_Question_Programming,
            "code-review" => LocaleKey.Oobe_Preview_Question_CodeReview,
            "writing-editing" => LocaleKey.Oobe_Preview_Question_Writing,
            "data-analysis" => LocaleKey.Oobe_Preview_Question_DataAnalysis,
            "learning-education" => LocaleKey.Oobe_Preview_Question_Learning,
            "translation-language" => LocaleKey.Oobe_Preview_Question_Translation,
            "product-planning" => LocaleKey.Oobe_Preview_Question_Product,
            "troubleshooting" => LocaleKey.Oobe_Preview_Question_Troubleshooting,
            "research-planning" => LocaleKey.Oobe_Preview_Question_Research,
            _ => LocaleKey.Oobe_Preview_Question_Default
        };

        var isConcise = string.Equals(snapshot.DetailLevelId, "concise", StringComparison.Ordinal);
        var isDetailed = string.Equals(snapshot.DetailLevelId, "detailed", StringComparison.Ordinal);
        var replyKeyName = (snapshot.ToneId, isConcise) switch
        {
            ("concise-direct", true) => LocaleKey.Oobe_Preview_Reply_ConciseDirect_Concise,
            ("concise-direct", false) => LocaleKey.Oobe_Preview_Reply_ConciseDirect_Rich,
            ("socratic-guiding", true) => LocaleKey.Oobe_Preview_Reply_Socratic_Concise,
            ("socratic-guiding", false) => LocaleKey.Oobe_Preview_Reply_Socratic_Rich,
            ("formal-business", true) => LocaleKey.Oobe_Preview_Reply_Formal_Concise,
            ("formal-business", false) => LocaleKey.Oobe_Preview_Reply_Formal_Rich,
            ("creative-exploratory", true) => LocaleKey.Oobe_Preview_Reply_Creative_Concise,
            ("creative-exploratory", false) => LocaleKey.Oobe_Preview_Reply_Creative_Rich,
            ("friendly-patient", true) => LocaleKey.Oobe_Preview_Reply_Friendly_Concise,
            ("friendly-patient", false) => LocaleKey.Oobe_Preview_Reply_Friendly_Rich,
            (_, true) => LocaleKey.Oobe_Preview_Reply_Professional_Concise,
            _ => LocaleKey.Oobe_Preview_Reply_Professional_Rich
        };

        IDynamicLocaleKey replyKey = new DynamicLocaleKey(replyKeyName);

        var personaWrapKeyName = snapshot.PersonaId switch
        {
            "professional" => LocaleKey.Oobe_Preview_Persona_ProfessionalWrap,
            "programming" => LocaleKey.Oobe_Preview_Persona_ProgrammingWrap,
            "writing" => LocaleKey.Oobe_Preview_Persona_WritingWrap,
            "translation" => LocaleKey.Oobe_Preview_Persona_TranslationWrap,
            "research" => LocaleKey.Oobe_Preview_Persona_ResearchWrap,
            "learning" => LocaleKey.Oobe_Preview_Persona_LearningWrap,
            "product" => LocaleKey.Oobe_Preview_Persona_ProductWrap,
            _ => null
        };
        if (personaWrapKeyName is not null)
        {
            replyKey = new FormattedDynamicLocaleKey(personaWrapKeyName, replyKey);
        }

        var organizationWrapKeyName = snapshot.OrganizationId switch
        {
            "conclusion-first" => LocaleKey.Oobe_Preview_Organization_ConclusionFirstWrap,
            "step-by-step" => LocaleKey.Oobe_Preview_Organization_StepByStepWrap,
            "key-points" => LocaleKey.Oobe_Preview_Organization_KeyPointsWrap,
            "compare-options" => LocaleKey.Oobe_Preview_Organization_CompareOptionsWrap,
            "examples-first" => LocaleKey.Oobe_Preview_Organization_ExamplesFirstWrap,
            "natural-conversation" => LocaleKey.Oobe_Preview_Organization_NaturalWrap,
            _ => null
        };
        if (organizationWrapKeyName is not null)
        {
            replyKey = new FormattedDynamicLocaleKey(organizationWrapKeyName, replyKey);
        }

        if (isDetailed)
        {
            replyKey = new FormattedDynamicLocaleKey(
                LocaleKey.Oobe_Preview_Reply_DetailedWrap,
                replyKey,
                new DynamicLocaleKey(LocaleKey.Oobe_Preview_DetailedAddendum));
        }

        var userName = snapshot.PreferredUserName?.Trim();
        if (!string.IsNullOrEmpty(userName))
        {
            replyKey = new FormattedDynamicLocaleKey(
                LocaleKey.Oobe_Preview_GreetingWrap,
                new DirectLocaleKey(userName),
                replyKey);
        }

        return new PromptRecipePreview(new DynamicLocaleKey(questionKeyName), replyKey);
    }
}
