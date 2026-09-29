using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.ProcessIsolation.Automation;

namespace Everywhere.Interop;

/// <summary>Reports a definitive failure while restoring saved monitoring intent.</summary>
public delegate void TextSelectionMonitoringRejectedHandler(TextSelectionMonitoringControlResult result);

/// <summary>Reports an expected rejection of a user-requested monitoring transition.</summary>
public sealed class TextSelectionMonitoringRejectedException(TextSelectionMonitoringControlResult result) : Exception
{
    /// <summary>Gets the structured rejection returned by Automation Host.</summary>
    public TextSelectionMonitoringControlResult Result { get; } = result;
}

/// <summary>
/// Publishes text selections detected through platform accessibility and input facilities.
/// </summary>
/// <remarks>
/// Process-isolated implementations accept one observer because each result may own a remote Anchor. Subscription and acknowledged native enablement have separate lifetimes.
/// </remarks>
public interface ITextSelectionWatcher : IObservable<TextSelectionData>, INotifyPropertyChanged
{
    /// <summary>Gets whether monitoring is desired across the current and future Host connections.</summary>
    bool IsEnabled { get; }

    /// <summary>Gets the process-wide, non-concurrent operation used to change monitoring.</summary>
    IAsyncRelayCommand<bool> SetEnabledCommand { get; }

    /// <summary>Raised when restoring saved intent on a replacement Host is definitively rejected.</summary>
    event TextSelectionMonitoringRejectedHandler? RestorationRejected;

    /// <summary>Restores saved enablement and policy without requiring the current Host connection to acknowledge them synchronously.</summary>
    void RestoreState(bool isEnabled, TextSelectionMonitoringConfiguration configuration);

    /// <summary>Replaces the latest policy without changing monitoring enablement.</summary>
    void UpdateConfiguration(TextSelectionMonitoringConfiguration configuration);
}