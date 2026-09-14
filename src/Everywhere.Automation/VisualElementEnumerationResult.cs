using System.Diagnostics.CodeAnalysis;

namespace Everywhere.Automation;

/// <summary>
/// Contains either one successfully enumerated element observation or one terminal relation failure.
/// </summary>
public readonly record struct VisualElementEnumerationResult
{
    /// <summary>
    /// Gets the element observation when relation advancement succeeded.
    /// </summary>
    public VisualElementQueryResult? Result { get; }

    /// <summary>
    /// Gets the relation-advancement failure when no element could be produced.
    /// </summary>
    public VisualElementQueryFailure? Failure { get; }

    /// <summary>
    /// Gets whether this item contains an element observation.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Result))]
    public bool IsSuccess => Result is not null;

    /// <summary>
    /// Initializes a successful relation item.
    /// </summary>
    public VisualElementEnumerationResult(VisualElementQueryResult result)
    {
        Result = result;
        Failure = null;
    }

    /// <summary>
    /// Initializes a terminal relation failure.
    /// </summary>
    public VisualElementEnumerationResult(VisualElementQueryFailure failure)
    {
        Result = null;
        Failure = failure;
    }

    /// <summary>
    /// Deconstructs this item for natural relation-processing loops.
    /// </summary>
    public void Deconstruct(out VisualElementQueryResult? result, out VisualElementQueryFailure? failure)
    {
        result = Result;
        failure = Failure;
    }
}