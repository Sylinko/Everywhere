using Everywhere.ProcessIsolation.Automation;

namespace Everywhere.Linux.Desktop;

/// <summary>
/// Marks text-selection monitoring as unported without starting PRIMARY or toolkit listeners.
/// </summary>
public sealed class UnavailableTextSelectionMonitorFactory : ITextSelectionMonitorFactory
{
    /// <inheritdoc />
    public ITextSelectionMonitor Create(
        ITextSelectionMonitorContext context,
        int mainProcessId,
        TextSelectionMonitoringConfiguration configuration,
        TextSelectionDiagnostics diagnostics,
        Func<TextSelectionObservation, CancellationToken, ValueTask> publish)
    {
        throw new NotSupportedException("Text-selection monitoring has not been ported to this desktop backend.");
    }
}