namespace Everywhere.Harness;

/// <summary>
/// Well-known metadata keys persisted on reasoning and function-call spans to preserve
/// provider-specific continuation state without duplicating visible content.
/// </summary>
public static class ReasoningMetadata
{
    public const string DialectKey = "dialect";
    public const string ProtectedDataKey = "protected";
    public const string ItemIdKey = "itemId";
    public const string IsRedactedKey = "redacted";
    public const string GeminiStepKey = "gemini.step";
    public const string ThoughtSignatureKey = "thoughtSignature";
    public const string BlockBoundaryKey = "harness.endBlock";
}
