using Everywhere.Chat;

namespace Everywhere.StrategyEngine.Conditions;

/// <summary>
/// Checks if the context has any attachments.
/// </summary>
public sealed class HasAttachmentsCondition : IStrategyCondition
{
    /// <summary>
    /// Minimum number of attachments required.
    /// </summary>
    public int MinCount { get; init; } = 1;

    /// <summary>
    /// Optional type filter.
    /// </summary>
    public AttachmentType? Type { get; init; }

    public bool Evaluate(StrategyContext context)
    {
        var attachments = Type switch
        {
            AttachmentType.VisualElement => context.Attachments.OfType<VisualElementAttachment>().Cast<ChatAttachment>(),
            AttachmentType.TextSelection => context.Attachments.OfType<TextSelectionAttachment>(),
            AttachmentType.Text => context.Attachments.OfType<TextAttachment>(),
            AttachmentType.File => context.Attachments.OfType<FileAttachment>(),
            _ => context.Attachments
        };

        return attachments.Count() >= MinCount;
    }
}