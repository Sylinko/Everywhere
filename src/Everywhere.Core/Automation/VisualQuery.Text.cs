using System.Text;
using Everywhere.Prompting;
using Everywhere.Prompting.Documents;

namespace Everywhere.Automation;

/// <summary>
/// Executes a bounded text query over a retained visual target and projects the result for an Agent.
/// </summary>
public sealed partial class VisualQuery
{
    /// <summary>Gets the default maximum text page length in UTF-16 code units.</summary>
    public const int DefaultTextLimit = 4_096;

    /// <summary>Gets the largest text page length accepted from an Agent call.</summary>
    public const int MaximumTextLimit = 16_384;

    /// <summary>Gets the largest text-stream offset accepted from an Agent call.</summary>
    public const int MaximumTextOffset = 16 * 1_024 * 1_024;

    private const int PromptTokenBudget = 10_240;

    /// <summary>
    /// Reads and projects one atomic text page using a stateless UTF-16 offset.
    /// </summary>
    /// <param name="targetId">The positive Agent-visible ID to resolve in this Context.</param>
    /// <param name="offset">The zero-based UTF-16 offset in the target's current logical text stream.</param>
    /// <param name="limit">The requested maximum page length in UTF-16 code units.</param>
    /// <returns>A compact model-facing text page with continuation and status metadata.</returns>
    /// <remarks>The live text may change between calls, so a continuation can overlap or omit concurrently edited content.</remarks>
    public string ReadText(int targetId, int offset = 0, int limit = DefaultTextLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetId);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, MaximumTextOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var outcome = ReadTextPage(ResolveTarget(targetId), offset, Math.Min(limit, MaximumTextLimit));
        var nextOffset = outcome.NextOffset is <= MaximumTextOffset ? outcome.NextOffset : null;
        var status = string.Join("; ", outcome.Status);
        if (outcome.NextOffset is > MaximumTextOffset)
        {
            status = string.IsNullOrEmpty(status) ?
                "The text-read offset safety limit was reached." :
                $"{status}; The text-read offset safety limit was reached.";
        }

        var element = new PromptCompactElement("visual-text", string.IsNullOrEmpty(outcome.Text) ? null : new PromptText(outcome.Text))
            .Attribute("target", targetId)
            .Attribute("offset", offset)
            .AttributeNotNull("next", nextOffset)
            .AttributeNotNullOrEmpty("status", status);
        if (TokenHelper.EstimateTokenCount(element.ToString()) > PromptTokenBudget)
        {
            element = new PromptCompactElement("visual-text")
                .Attribute("target", targetId)
                .Attribute("offset", offset)
                .Attribute("status", "The requested page cannot fit the local prompt budget. Retry the same offset with a smaller limit.");
        }

        return new PromptTokenLimit(PromptTokenBudget, element.Atomic()).ToString();
    }

    private static VisualTextQueryOutcome ReadTextPage(VisualTarget target, int offset, int maxCharacters)
    {
        var status = new List<string>();
        return target switch
        {
            ElementTarget elementTarget => ReadElement(elementTarget, offset, maxCharacters, status),
            CompositeTarget compositeTarget => ReadComposite(compositeTarget, offset, maxCharacters, status),
            _ => throw new NotSupportedException($"Visual target type '{target.GetType().Name}' does not expose readable text."),
        };
    }

    private static VisualTextQueryOutcome ReadElement(ElementTarget target, int offset, int maxCharacters, List<string> status)
    {
        var result = ReadElement(target.Element, offset, maxCharacters);
        if (result.Failure is { } failure)
        {
            AppendDistinct(status, GetFailureStatus(failure.Kind));
            return new VisualTextQueryOutcome(string.Empty, failure.Kind == VisualElementQueryFailureKind.Unsupported ? null : offset, status);
        }

        return new VisualTextQueryOutcome(result.Text ?? string.Empty, result.NextOffset, status);
    }

    private static VisualTextQueryOutcome ReadComposite(CompositeTarget target, int offset, int maxCharacters, List<string> status)
    {
        var prefixLength = (int)Math.Min((long)offset + maxCharacters + 2, int.MaxValue);
        var text = new StringBuilder(Math.Min(prefixLength, 256));
        var hasPreviousText = false;
        for (var partIndex = 0; partIndex < target.Parts.Count && text.Length < prefixLength; partIndex++)
        {
            var part = target.Parts[partIndex];
            var partOffset = 0;
            var hasWrittenPart = false;
            while (text.Length < prefixLength)
            {
                var result = ReadCompositePart(part, partOffset, prefixLength - text.Length);
                if (result.Failure is { } failure)
                {
                    AppendDistinct(status, $"Observed member {partIndex + 1}: {GetFailureStatus(failure.Kind)}");
                    if (failure.Kind != VisualElementQueryFailureKind.Unsupported) return new VisualTextQueryOutcome(string.Empty, offset, status);
                    break;
                }

                if (!string.IsNullOrEmpty(result.Text))
                {
                    if (!hasWrittenPart && hasPreviousText) text.Append(Environment.NewLine);
                    text.Append(result.Text);
                    hasWrittenPart = true;
                    hasPreviousText = true;
                }

                if (result.NextOffset is not { } nextOffset) break;
                if (nextOffset <= partOffset) throw new InvalidOperationException("A visual-element text read returned a non-advancing next offset.");
                partOffset = nextOffset;
            }
        }

        return CreateOutcome(text.ToString(), offset, maxCharacters, status);
    }

    private static VisualElementTextReadResult ReadCompositePart(CompositePart part, int offset, int maxCharacters)
    {
        var result = ReadElement(part.Element, offset, maxCharacters);
        if (result.Failure?.Kind != VisualElementQueryFailureKind.Unsupported || part.Snapshot.HasMoreText) return result;
        var observedText = !string.IsNullOrEmpty(part.Snapshot.TextPreview) ? part.Snapshot.TextPreview : part.Snapshot.Name;
        return observedText is null ? result : CreateObservedTextReadResult(observedText, offset, maxCharacters);
    }

    private static VisualElementTextReadResult ReadElement(VisualElement element, int offset, int maxCharacters)
    {
        try
        {
            return element.ReadText(offset, maxCharacters);
        }
        catch (TimeoutException exception)
        {
            return VisualElementTextReadResult.FromFailure(new VisualElementQueryFailure(VisualElementQueryFailureKind.Timeout, null, exception));
        }
        catch (ObjectDisposedException exception)
        {
            return VisualElementTextReadResult.FromFailure(
                new VisualElementQueryFailure(VisualElementQueryFailureKind.ElementUnavailable, null, exception));
        }
        catch (NotSupportedException exception)
        {
            return VisualElementTextReadResult.FromFailure(new VisualElementQueryFailure(VisualElementQueryFailureKind.Unsupported, null, exception));
        }
        catch (InvalidOperationException exception)
        {
            return VisualElementTextReadResult.FromFailure(
                new VisualElementQueryFailure(VisualElementQueryFailureKind.ProviderFailure, null, exception));
        }
    }

    private static VisualElementTextReadResult CreateObservedTextReadResult(string text, int offset, int maxCharacters)
    {
        if (offset >= text.Length) return new VisualElementTextReadResult(string.Empty, null, null);
        var end = (int)Math.Min((long)offset + maxCharacters, text.Length);
        return new VisualElementTextReadResult(text[offset..end], end < text.Length ? end : null, null);
    }

    private static VisualTextQueryOutcome CreateOutcome(string text, int offset, int maxCharacters, IReadOnlyList<string> status)
    {
        if (offset >= text.Length) return new VisualTextQueryOutcome(string.Empty, null, status);
        var end = (int)Math.Min((long)offset + maxCharacters, text.Length);
        if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end = end - offset == 1 ? end + 1 : end - 1;
        return new VisualTextQueryOutcome(text[offset..end], end < text.Length ? end : null, status);
    }

    private static string GetFailureStatus(VisualElementQueryFailureKind kind) => kind switch
    {
        VisualElementQueryFailureKind.Timeout => "Text reading timed out.",
        VisualElementQueryFailureKind.ElementUnavailable => "The visual element became unavailable while reading text.",
        VisualElementQueryFailureKind.Unsupported => "The visual element does not expose readable text.",
        _ => "Text reading failed in the platform provider.",
    };

    private static void AppendDistinct(List<string> destination, string item)
    {
        if (!string.IsNullOrWhiteSpace(item) && !destination.Contains(item, StringComparer.Ordinal)) destination.Add(item);
    }

    /// <summary>
    /// Contains one operation-local text page before it is projected into an Agent-facing prompt node.
    /// </summary>
    /// <param name="Text">The complete bounded page.</param>
    /// <param name="NextOffset">The next UTF-16 offset, or <see langword="null" /> when this observation found no more text.</param>
    /// <param name="Status">Best-effort explanations for degraded or failed observations.</param>
    private sealed record VisualTextQueryOutcome(string Text, int? NextOffset, IReadOnlyList<string> Status);
}