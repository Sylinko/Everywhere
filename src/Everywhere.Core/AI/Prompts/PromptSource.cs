namespace Everywhere.AI.Prompts;

/// <summary>
/// Describes how a prompt was initially created.
/// </summary>
/// <remarks>
/// The source is metadata for UI, diagnostics, and migration auditing. It is not part of prompt
/// rendering semantics; two prompts with identical templates render the same regardless of source.
/// </remarks>
public enum PromptSource
{
    [DynamicLocaleKey(LocaleKey.PromptSource_Blank)]
    Blank,

    [DynamicLocaleKey(LocaleKey.PromptSource_Guided)]
    Guided,

    [DynamicLocaleKey(LocaleKey.PromptSource_Copy)]
    Copy,

    [DynamicLocaleKey(LocaleKey.PromptSource_Migration)]
    Migration,

    [DynamicLocaleKey(LocaleKey.PromptSource_Import)]
    Import
}
