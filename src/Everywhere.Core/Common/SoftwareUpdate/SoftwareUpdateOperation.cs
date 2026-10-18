namespace Everywhere.Common;

public enum SoftwareUpdateOperationIntent
{
    Automatic,
    Manual
}

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
    long Id,
    SoftwareUpdateOperationIntent Intent,
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

    public bool IsIndeterminate => Stage != SoftwareUpdateOperationStage.Downloading || double.IsNaN(Progress);

    public bool CanCancel =>
        (Stage is SoftwareUpdateOperationStage.Checking or
            SoftwareUpdateOperationStage.Downloading or
            SoftwareUpdateOperationStage.Validating) &&
        (Intent == SoftwareUpdateOperationIntent.Manual ||
            (Intent == SoftwareUpdateOperationIntent.Automatic && Stage == SoftwareUpdateOperationStage.Downloading));
}

internal sealed class SoftwareUpdateConsistencyException(string message) : Exception(message);
