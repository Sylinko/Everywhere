using System.Text.RegularExpressions;
using Everywhere.Chat;
using ZLinq;

namespace Everywhere.StrategyEngine.Conditions;

/// <summary>
/// Condition that matches <see cref="TextSelectionAttachment"/> or <see cref="TextAttachment"/>.
/// </summary>
public sealed class TextCondition : IAttachmentCondition
{
    public AttachmentType TargetType { get; init; } = AttachmentType.TextSelection;

    /// <summary>
    /// If true, at least one matching attachment must be primary.
    /// </summary>
    public bool IsPrimaryRequired { get; init; }

    /// <summary>
    /// Regex pattern to match against the text content.
    /// </summary>
    public Regex? TextPattern { get; init; }

    /// <summary>
    /// Simple contains check (any of these strings must be present).
    /// </summary>
    public IReadOnlyList<string>? TextContains { get; init; }

    /// <summary>
    /// Minimum text length required.
    /// </summary>
    public int? MinLength { get; init; }

    /// <summary>
    /// Maximum text length allowed.
    /// </summary>
    public int? MaxLength { get; init; }

    /// <summary>
    /// Minimum number of matching attachments required.
    /// </summary>
    public int MinCount { get; init; } = 1;

    public bool Evaluate(StrategyContext context)
    {
        var matchingCount = 0;
        var hasPrimary = false;

        foreach (var attachment in context.Attachments)
        {
            var text = GetText(attachment);
            if (text is null)
            {
                continue;
            }

            if (!MatchesText(text))
            {
                continue;
            }

            matchingCount++;
            if (attachment.IsPrimary)
            {
                hasPrimary = true;
            }
        }

        if (matchingCount < MinCount)
        {
            return false;
        }

        if (IsPrimaryRequired && !hasPrimary)
        {
            return false;
        }

        return true;
    }

    private string? GetText(ChatAttachment attachment)
    {
        return attachment switch
        {
            TextSelectionAttachment textSelection
                when TargetType is AttachmentType.TextSelection or AttachmentType.Any =>
                textSelection.Text,

            TextAttachment textAttachment
                when TargetType is AttachmentType.Text or AttachmentType.Any =>
                textAttachment.Text,

            _ => null
        };
    }

    private bool MatchesText(string text)
    {
        // Check length constraints
        if (MinLength.HasValue && text.Length < MinLength.Value)
        {
            return false;
        }

        if (MaxLength.HasValue && text.Length > MaxLength.Value)
        {
            return false;
        }

        // Check regex pattern
        if (TextPattern is not null && !TextPattern.IsMatch(text))
        {
            return false;
        }

        // Check contains
        return TextContains?.AsValueEnumerable().Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase)) is true;
    }
}
