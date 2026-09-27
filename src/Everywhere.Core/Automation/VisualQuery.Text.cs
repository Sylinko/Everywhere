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

    /// <summary>Gets the largest absolute text-stream offset accepted from an Agent call.</summary>
    public const int MaximumTextOffset = VisualTextReadLimits.MaximumTextReadProbeCharacters - MaximumTextLimit;

    private const int PromptTokenBudget = 10_240;
    private const string ProbeLimitStatus =
        "The platform text-read limit was reached; total is a lower bound";
    private const string EndRelativeProbeLimitStatus =
        "The platform text-read limit was reached; this negative offset is relative to the observed prefix, not the actual end";

    /// <summary>
    /// Reads and projects one atomic text page using a stateless UTF-16 offset.
    /// </summary>
    /// <param name="targetId">The positive Agent-visible ID to resolve in this Context.</param>
    /// <param name="offset">The UTF-16 offset in the target's current logical text stream. A negative value is resolved from the current end.</param>
    /// <param name="limit">The requested maximum page length in UTF-16 code units.</param>
    /// <returns>A compact model-facing text page with continuation and status metadata.</returns>
    /// <remarks>The live text may change between calls, so a continuation can overlap or omit concurrently edited content.</remarks>
    public string ReadText(int targetId, int offset = 0, int limit = DefaultTextLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetId);
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, -MaximumTextOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, MaximumTextOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var outcome = ReadTextPage(ResolveTarget(targetId), offset, Math.Min(limit, MaximumTextLimit));
        var nextOffset = outcome.NextOffset is <= MaximumTextOffset ? outcome.NextOffset : null;
        var status = string.Join("; ", outcome.Status);
        if (outcome.NextOffset is > MaximumTextOffset)
        {
            status = string.IsNullOrEmpty(status) ?
                "The text-read offset safety limit was reached" :
                $"{status}; The text-read offset safety limit was reached";
        }

        var element = new PromptCompactElement("visual-text", string.IsNullOrEmpty(outcome.Text) ? null : new PromptText(outcome.Text))
            .Attribute("target", targetId)
            .Attribute("offset", outcome.Offset)
            .AttributeNotNull("next", nextOffset)
            .AttributeNotNullOrEmpty("total", outcome.TotalLength is { } totalLength ? VisualTextLengthFormatting.Format(totalLength) : null)
            .AttributeNotNullOrEmpty("status", status);
        if (TokenHelper.EstimateTokenCount(element.ToString()) > PromptTokenBudget)
        {
            element = new PromptCompactElement("visual-text")
                .Attribute("target", targetId)
                .Attribute("offset", outcome.Offset)
                .AttributeNotNullOrEmpty(
                    "total",
                    outcome.TotalLength is { } oversizedTotal ? VisualTextLengthFormatting.Format(oversizedTotal) : null)
                .Attribute("status", "The requested page cannot fit the local prompt budget. Retry the same offset with a smaller limit");
        }

        return new PromptTokenLimit(PromptTokenBudget, element.Atomic()).ToString();
    }

    private static VisualTextQueryOutcome ReadTextPage(VisualTarget target, int offset, int maxCharacters)
    {
        var status = new List<string>();
        return target switch
        {
            ElementTarget elementTarget => ReadElement(elementTarget, offset, maxCharacters, status),
            CompositeTarget compositeTarget when offset < 0 => ReadCompositeFromEnd(compositeTarget, offset, maxCharacters, status),
            CompositeTarget compositeTarget => ReadCompositeForward(compositeTarget, offset, maxCharacters, status),
            _ => throw new NotSupportedException($"Visual target type '{target.GetType().Name}' does not expose readable text."),
        };
    }

    private static VisualTextQueryOutcome ReadElement(ElementTarget target, int offset, int maxCharacters, List<string> status)
    {
        var result = ReadElement(target.Element, offset, maxCharacters, VisualTextReadLimits.MaximumTextReadProbeCharacters);
        if (result.Failure is { } failure)
        {
            AppendDistinct(status, GetFailureStatus(failure));
            return new VisualTextQueryOutcome(string.Empty, offset, null, result.TotalLength, status);
        }

        var totalLength = GetObservedLength(result);
        if (result.NextOffset is { } nextOffset)
        {
            totalLength = VisualTextLengthFormatting.EnsureLowerBound(totalLength, VisualTextLengthFormatting.SaturatingIncrement(nextOffset));
        }

        if (result.IsEndRelativeOffsetBounded)
        {
            AppendDistinct(status, EndRelativeProbeLimitStatus);
        }
        else if (totalLength.Kind == VisualTextLengthKind.LowerBound)
        {
            AppendDistinct(status, ProbeLimitStatus);
        }
        return new VisualTextQueryOutcome(result.Text ?? string.Empty, result.Offset, result.NextOffset, totalLength, status);
    }

    private static VisualTextQueryOutcome ReadCompositeForward(
        CompositeTarget target,
        int offset,
        int maxCharacters,
        List<string> status)
    {
        var page = new StringBuilder(Math.Min(maxCharacters + 1, 256));
        var remainingProbeCharacters = VisualTextReadLimits.MaximumTextReadProbeCharacters;
        var observedLength = 0L;
        var hasPreviousText = false;
        var isComplete = true;

        for (var partIndex = 0; partIndex < target.Parts.Count; partIndex++)
        {
            if (remainingProbeCharacters == 0)
            {
                isComplete = false;
                AppendDistinct(status, ProbeLimitStatus);
                break;
            }

            var potentialPartStart = observedLength + (hasPreviousText ? Environment.NewLine.Length : 0);
            var localOffset = page.Length >= maxCharacters || offset <= potentialPartStart ?
                0 :
                (int)Math.Min(offset - potentialPartStart, int.MaxValue);
            var requestedCharacters = page.Length >= maxCharacters ? 1 : Math.Max(1, maxCharacters - page.Length);
            var result = ReadCompositePart(target.Parts[partIndex], localOffset, requestedCharacters, remainingProbeCharacters);
            remainingProbeCharacters = ConsumeProbeBudget(remainingProbeCharacters, result.ProbedLength);
            if (result.Failure is { } failure)
            {
                AppendDistinct(status, $"Member {partIndex + 1}: {GetFailureStatus(failure)}");
                isComplete = false;
                break;
            }

            var partLength = GetObservedLength(result);
            if (partLength.Value == 0) continue;

            if (hasPreviousText)
            {
                AppendSegment(page, Environment.NewLine, observedLength, offset, maxCharacters);
                observedLength += Environment.NewLine.Length;
            }

            AppendSegment(page, result.Text ?? string.Empty, observedLength + result.Offset, offset, maxCharacters);
            observedLength += partLength.Value;
            hasPreviousText = true;

            if (partLength.Kind == VisualTextLengthKind.LowerBound)
            {
                isComplete = false;
                AppendDistinct(status, ProbeLimitStatus);
                break;
            }
        }

        var totalLength = CreateCompositeLength(observedLength, isComplete);
        var pageEnd = (long)offset + page.Length;
        var nextOffset = pageEnd < observedLength && pageEnd <= int.MaxValue ? (int?)pageEnd : null;
        return new VisualTextQueryOutcome(page.ToString(), offset, nextOffset, totalLength, status);
    }

    private static VisualTextQueryOutcome ReadCompositeFromEnd(
        CompositeTarget target,
        int offset,
        int maxCharacters,
        List<string> status)
    {
        // Keep one code unit before the requested position so a tail cut cannot hide the
        // high surrogate that belongs to a low surrogate at the requested boundary.
        var tailCapacity = checked(-offset + 1);
        var tail = new BoundedTailBuffer(tailCapacity);
        var remainingProbeCharacters = VisualTextReadLimits.MaximumTextReadProbeCharacters;
        var observedLength = 0L;
        var hasPreviousText = false;
        var isComplete = true;

        for (var partIndex = 0; partIndex < target.Parts.Count; partIndex++)
        {
            if (remainingProbeCharacters == 0)
            {
                isComplete = false;
                AppendDistinct(status, EndRelativeProbeLimitStatus);
                break;
            }

            // A member may move its start back by one code unit to preserve a surrogate pair.
            // Allow that extra output so the retained suffix still reaches the member's end.
            var result = ReadCompositePart(target.Parts[partIndex], -tailCapacity, tailCapacity + 1, remainingProbeCharacters);
            remainingProbeCharacters = ConsumeProbeBudget(remainingProbeCharacters, result.ProbedLength);
            if (result.Failure is { } failure)
            {
                AppendDistinct(status, $"Member {partIndex + 1}: {GetFailureStatus(failure)}");
                isComplete = false;
                break;
            }

            var partLength = GetObservedLength(result);
            if (partLength.Value == 0) continue;

            if (hasPreviousText)
            {
                tail.Append(Environment.NewLine);
                observedLength += Environment.NewLine.Length;
            }

            tail.Append(result.Text ?? string.Empty);
            observedLength += partLength.Value;
            hasPreviousText = true;

            if (partLength.Kind == VisualTextLengthKind.LowerBound)
            {
                isComplete = false;
                AppendDistinct(status, EndRelativeProbeLimitStatus);
                break;
            }
        }

        var resolvedOffsetLong = Math.Max(0L, observedLength + offset);
        var tailStart = observedLength - tail.Length;
        var startInTail = (int)Math.Max(0L, resolvedOffsetLong - tailStart);
        var available = Math.Max(0, tail.Length - startInTail);
        var pageLength = Math.Min(maxCharacters, available);
        var originalStartInTail = startInTail;
        AdjustSurrogatePageBoundary(tail, ref startInTail, ref pageLength);
        resolvedOffsetLong -= originalStartInTail - startInTail;
        var text = tail.Slice(startInTail, pageLength);
        var resolvedOffset = resolvedOffsetLong <= int.MaxValue ? (int)resolvedOffsetLong : int.MaxValue;
        if (resolvedOffsetLong > int.MaxValue)
        {
            isComplete = false;
            AppendDistinct(status, "The text-read offset representation limit was reached");
        }

        var pageEnd = resolvedOffsetLong + text.Length;
        var nextOffset = pageEnd < observedLength && pageEnd <= int.MaxValue ? (int?)pageEnd : null;
        return new VisualTextQueryOutcome(text, resolvedOffset, nextOffset, CreateCompositeLength(observedLength, isComplete), status);
    }

    private static void AppendSegment(StringBuilder page, string segment, long segmentOffset, int requestedOffset, int maxCharacters)
    {
        if (segment.Length == 0 || page.Length >= maxCharacters + 1) return;
        var requestedEnd = (long)requestedOffset + maxCharacters;
        var start = (int)Math.Max(0L, requestedOffset - segmentOffset);
        var end = (int)Math.Min(segment.Length, requestedEnd - segmentOffset);
        if (end <= start) return;
        if (end < segment.Length && char.IsHighSurrogate(segment[end - 1]) && char.IsLowSurrogate(segment[end]))
        {
            end = end - start == 1 ? end + 1 : end - 1;
        }

        page.Append(segment.AsSpan(start, end - start));
    }

    private static void AdjustSurrogatePageBoundary(BoundedTailBuffer tail, ref int start, ref int length)
    {
        if (length == 0) return;
        if (start > 0 && char.IsLowSurrogate(tail[start]) && char.IsHighSurrogate(tail[start - 1]))
        {
            start--;
            length++;
        }

        var end = start + length;
        if (end < tail.Length && char.IsHighSurrogate(tail[end - 1]) && char.IsLowSurrogate(tail[end]))
        {
            length = length == 1 ? length + 1 : length - 1;
        }
    }

    private static VisualElementTextReadResult ReadCompositePart(
        CompositePart part,
        int offset,
        int maxCharacters,
        int maximumProbeCharacters)
    {
        if (part.ContentSource == CompositePartContentSource.Text)
        {
            return ReadElement(part.Element, offset, maxCharacters, maximumProbeCharacters);
        }
        if (part.ContentSource != CompositePartContentSource.Name)
        {
            throw new ArgumentOutOfRangeException(nameof(part.ContentSource), part.ContentSource, null);
        }

        try
        {
            var nameResult = part.Element.Query(new VisualElementQueryRequest(VisualElementFields.Name, 0));
            if (nameResult.Failure is { } failure) return VisualElementTextReadResult.FromFailure(failure);
            var name = string.IsNullOrWhiteSpace(nameResult.Snapshot.Name) ? string.Empty : nameResult.Snapshot.Name;
            return VisualElementTextReadResult.FromSuccess(name, offset, maxCharacters);
        }
        catch (UnauthorizedAccessException exception)
        {
            return VisualElementTextReadResult.FromFailure(
                new VisualElementQueryFailure(VisualElementQueryFailureKind.PermissionDenied, null, exception));
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
            return VisualElementTextReadResult.FromFailure(
                new VisualElementQueryFailure(VisualElementQueryFailureKind.Unsupported, null, exception));
        }
        catch (InvalidOperationException exception)
        {
            return VisualElementTextReadResult.FromFailure(
                new VisualElementQueryFailure(VisualElementQueryFailureKind.ProviderFailure, null, exception));
        }
    }

    private static VisualElementTextReadResult ReadElement(
        VisualElement element,
        int offset,
        int maxCharacters,
        int maximumProbeCharacters)
    {
        try
        {
            return element.ReadText(offset, maxCharacters, maximumProbeCharacters);
        }
        catch (UnauthorizedAccessException exception)
        {
            return VisualElementTextReadResult.FromFailure(
                new VisualElementQueryFailure(VisualElementQueryFailureKind.PermissionDenied, null, exception));
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

    private static VisualTextLength GetObservedLength(VisualElementTextReadResult result)
    {
        if (result.TotalLength is { } totalLength) return totalLength;
        if (result.NextOffset is { } nextOffset)
        {
            return VisualTextLength.LowerBound(VisualTextLengthFormatting.SaturatingIncrement(nextOffset));
        }

        var observedEnd = (int)Math.Min((long)result.Offset + (result.Text?.Length ?? 0), int.MaxValue);
        return VisualTextLength.Exact(Math.Max(observedEnd, result.ProbedLength));
    }

    private static VisualTextLength CreateCompositeLength(long observedLength, bool isComplete)
    {
        if (observedLength > int.MaxValue) return VisualTextLength.LowerBound(int.MaxValue);
        return isComplete ? VisualTextLength.Exact((int)observedLength) : VisualTextLength.LowerBound((int)observedLength);
    }

    private static int ConsumeProbeBudget(int remaining, int consumed) => Math.Max(0, remaining - Math.Max(0, consumed));

    private static string GetFailureStatus(VisualElementQueryFailure failure)
    {
        if (!string.IsNullOrWhiteSpace(failure.AgentMessage)) return failure.AgentMessage;
        return failure.Kind switch
        {
            VisualElementQueryFailureKind.PermissionDenied => "Permission to read text from the visual element was denied",
            VisualElementQueryFailureKind.Timeout => "Text reading timed out",
            VisualElementQueryFailureKind.ElementUnavailable => "The visual element became unavailable while reading text",
            VisualElementQueryFailureKind.Unsupported => "The visual element does not expose readable text",
            VisualElementQueryFailureKind.LimitReached => "Text reading reached a platform limit",
            _ => "Text reading failed in the platform provider",
        };
    }

    private static void AppendDistinct(List<string> destination, string item)
    {
        if (!string.IsNullOrWhiteSpace(item) && !destination.Contains(item, StringComparer.Ordinal)) destination.Add(item);
    }

    /// <summary>
    /// Contains one operation-local text page before it is projected into an Agent-facing prompt node.
    /// </summary>
    private sealed record VisualTextQueryOutcome(
        string Text,
        int Offset,
        int? NextOffset,
        VisualTextLength? TotalLength,
        IReadOnlyList<string> Status
    );

    private sealed class BoundedTailBuffer(int capacity)
    {
        public int Length => _value.Length;

        private readonly StringBuilder _value = new(Math.Min(capacity, 256));

        public char this[int index] => _value[index];

        public void Append(string value)
        {
            if (value.Length >= capacity)
            {
                _value.Clear();
                _value.Append(value.AsSpan(value.Length - capacity));
                return;
            }

            _value.Append(value);
            var excess = _value.Length - capacity;
            if (excess > 0) _value.Remove(0, excess);
        }

        public string Slice(int start, int length) => length == 0 ? string.Empty : _value.ToString(start, length);
    }
}
