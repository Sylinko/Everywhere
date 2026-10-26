namespace Everywhere.Linux.Platform;

/// <summary>
/// Converts wheel notches and touchpad deltas into deliberate selection-mode steps.
/// One gesture cannot cycle through every mode before the user sees the first change.
/// </summary>
public sealed class SelectionModeWheel
{
    private const long GestureGapMilliseconds = 250;
    private const long StepIntervalMilliseconds = 180;
    private double _accumulatedDelta;
    private long _lastEvent;
    private long? _lastStep;

    /// <summary>
    /// Returns the previous/next mode step, or zero while accumulating a gesture.
    /// </summary>
    public int Update(double verticalDelta, long timestampMilliseconds)
    {
        if (!double.IsFinite(verticalDelta) || verticalDelta == 0)
        {
            return 0;
        }

        if (timestampMilliseconds - _lastEvent > GestureGapMilliseconds ||
            Math.Sign(verticalDelta) != Math.Sign(_accumulatedDelta))
        {
            _accumulatedDelta = 0;
        }

        _lastEvent = timestampMilliseconds;
        if (_lastStep is { } previous && timestampMilliseconds - previous < StepIntervalMilliseconds)
        {
            return 0;
        }

        _accumulatedDelta += verticalDelta;
        if (Math.Abs(_accumulatedDelta) < 1)
        {
            return 0;
        }

        _accumulatedDelta = 0;
        _lastStep = timestampMilliseconds;
        return verticalDelta > 0 ? -1 : 1;
    }
}