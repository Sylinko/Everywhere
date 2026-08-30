namespace Everywhere.Interop;

/// <summary>
/// Publishes text selections detected through platform accessibility and input facilities.
/// </summary>
/// <remarks>Implementations should activate global hooks only while at least one observer is subscribed.</remarks>
public interface ITextSelectionWatcher : IObservable<TextSelectionData>;