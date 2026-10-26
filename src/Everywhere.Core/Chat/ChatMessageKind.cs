namespace Everywhere.Chat;

/// <summary>
/// Durable message-type projection used to inspect topology without loading payloads.
/// Roles do not define turn boundaries.
/// </summary>
// Keep these values aligned with the Union tags on ChatMessage by convention.
// There is no generated or compiler-enforced link; update both declarations when adding a type.
public enum ChatMessageKind
{
    Root = 0,
    Assistant = 1,
    User = 2,
    Action = 3,
    FunctionCall = 4,
    UserStrategy = 5,
    UserAction = 6,
    Compression = 7,
    VisualContextReset = 8,
}

/// <summary>
/// Classifies message kinds without loading their bodies.
/// </summary>
public static class ChatMessageKinds
{
    /// <summary>
    /// Gets whether this kind starts a native user turn.
    /// </summary>
    public static bool IsTurnStart(this ChatMessageKind kind)
    {
        return kind is ChatMessageKind.User or ChatMessageKind.UserStrategy or ChatMessageKind.UserAction;
    }
}