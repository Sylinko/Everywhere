using Everywhere.Chat;
using ZLinq;

namespace Everywhere.StrategyEngine.Conditions;

/// <summary>
/// Base class for conditions that match specific attachment types.
/// </summary>
public abstract class AttachmentConditionBase<T> : IAttachmentCondition where T : ChatAttachment
{
    public abstract AttachmentType TargetType { get; }

    /// <summary>
    /// If true, at least one matching attachment must be primary.
    /// </summary>
    public bool IsPrimaryRequired { get; init; }

    /// <summary>
    /// Minimum number of matching attachments required.
    /// </summary>
    public int MinCount { get; init; } = 1;

    /// <summary>
    /// Maximum number of matching attachments allowed (-1 for unlimited).
    /// </summary>
    public int MaxCount { get; init; } = -1;

    public bool Evaluate(StrategyContext context)
    {
        var matchingAttachments = context.Attachments.AsValueEnumerable().OfType<T>().Where(MatchesAttachment).ToList();
        if (matchingAttachments.Count < MinCount)
        {
            return false;
        }

        if (MaxCount >= 0 && matchingAttachments.Count > MaxCount)
        {
            return false;
        }

        if (IsPrimaryRequired && !matchingAttachments.AsValueEnumerable().Any(a => a.IsPrimary))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Override to implement type-specific matching logic.
    /// </summary>
    protected abstract bool MatchesAttachment(T attachment);
}