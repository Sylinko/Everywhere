namespace Everywhere.Views;

/// <summary>
/// Controls one-shot and hover-triggered animation playback for an onboarding preview card.
/// </summary>
public interface IOobePreview
{
    /// <summary>
    /// Starts or restarts the preview animation from its opening state.
    /// </summary>
    void Play();

    /// <summary>
    /// Stops any active animation playback without changing the resting state.
    /// </summary>
    void Stop();

    /// <summary>
    /// Stops playback and restores the settled resting state.
    /// </summary>
    void Reset();
}
