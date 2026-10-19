namespace Everywhere.Common;

public enum SoftwareUpdateOperationStage
{
    Checking,
    Downloading,
    Validating,
    Cancelling,
    Installing,
    Completed,
    Canceled,
    Failed
}

/// <summary>
/// An immutable presentation snapshot of coordinator-owned update work.
/// </summary>
public sealed record SoftwareUpdateOperation(
    SoftwareUpdateOperationStage Stage,
    SoftwareUpdateMetadata? Target,
    double Progress = 0d,
    Exception? Error = null)
{
    public bool IsActive => Stage is
        SoftwareUpdateOperationStage.Checking or
        SoftwareUpdateOperationStage.Downloading or
        SoftwareUpdateOperationStage.Validating or
        SoftwareUpdateOperationStage.Cancelling or
        SoftwareUpdateOperationStage.Installing;

    public bool IsIndeterminate => Stage != SoftwareUpdateOperationStage.Downloading || !double.IsFinite(Progress) || Progress <= 0d;

    public bool CanCancel => Stage is
        SoftwareUpdateOperationStage.Checking or
        SoftwareUpdateOperationStage.Downloading or
        SoftwareUpdateOperationStage.Validating;
}

internal sealed class SoftwareUpdateConsistencyException(string message) : Exception(message);