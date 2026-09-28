namespace Everywhere.Views;

/// <summary>
/// Defines a contract for tracking a target point for particle effects, allowing for cancellation, completion, and handoff of the target point.
/// </summary>
public interface IParticleTargetTracker
{
    /// <summary>
    /// Gets a value indicating whether the tracking operation has been cancelled.
    /// </summary>
    bool IsCancelled { get; }

    /// <summary>
    /// Gets a value indicating whether the tracking operation has been completed.
    /// </summary>
    bool IsCompleted { get; }

    /// <summary>
    /// Attempts to retrieve the target center point on the screen.
    /// </summary>
    /// <param name="point"></param>
    /// <returns></returns>
    bool TryGetTargetCenterOnScreen(out PixelPoint point);

    /// <summary>
    /// Begins the acceptance of the target point for particle effects, returning true if the acceptance process has started successfully.
    /// </summary>
    /// <returns></returns>
    bool BeginAcceptance();

    /// <summary>
    /// Checks if the tracker is ready for handoff and retrieves the target point if it is.
    /// </summary>
    /// <param name="point"></param>
    /// <returns></returns>
    bool IsReadyForHandoff(out PixelPoint point);

    /// <summary>
    /// Begins the handoff process for the target point, allowing for the transfer of tracking responsibility to another component or system.
    /// </summary>
    void BeginHandoff();

    /// <summary>
    /// Cancels the tracking operation.
    /// </summary>
    void Cancel();
}