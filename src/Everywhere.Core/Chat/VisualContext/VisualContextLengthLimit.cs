namespace Everywhere.Chat;

public enum VisualContextLengthLimit
{
    /// <summary>
    /// 1024 tokens, suitable for short and focused interactions, such as answering a specific question about the UI or providing concise descriptions.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.VisualContextLengthLimit_Minimal)]
    Minimal = 0,

    /// <summary>
    /// (Recommended) 4096 tokens, a balanced option that captures more context and details while still being manageable for most LLMs.
    /// Ideal for general use cases where a comprehensive understanding of the UI is beneficial without overwhelming the model.
    /// This is the recommended default setting for most interactions, providing a good trade-off between context and performance.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.VisualContextLengthLimit_Balanced)]
    Balanced = 1,

    /// <summary>
    /// 10240 tokens, the most detailed option that includes extensive context and information about the UI.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.VisualContextLengthLimit_Detailed)]
    Detailed = 2,

    /// <summary>
    /// 40960 tokens, an extremely detailed option that captures an exhaustive representation of the UI, including all elements and their properties.
    /// This setting is intended for advanced use cases where a deep understanding of the entire UI is necessary, and the LLM can handle very large inputs.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.VisualContextLengthLimit_Ultimate)]
    Ultimate = 3,

    /// <summary>
    /// Unlimited tokens, no limit on the visual tree length. This setting should be used with caution, as it may lead to performance issues or exceed the input limits of the LLM.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.VisualContextLengthLimit_Unlimited)]
    Unlimited = 4
}