using System.Diagnostics;
using Everywhere.Chat;
using ZLinq;

namespace Everywhere.StrategyEngine;

/// <summary>
/// The context for strategy evaluation, containing all attachments and derived information.
/// </summary>
public sealed class StrategyContext
{
    /// <summary>
    /// User-provided attachments (files, text selections, visual elements).
    /// Use <see cref="ChatAttachment.IsPrimary"/> to identify focused items (0 or more).
    /// </summary>
    public required IReadOnlyList<ChatAttachment> Attachments { get; init; }

    /// <summary>
    /// Active process information (derived from visual elements).
    /// </summary>
    public ProcessInfo? ActiveProcess { get; init; }

    /// <summary>
    /// Additional metadata for custom matching logic.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();

    /// <summary>
    /// Creates a StrategyContext from a list of attachments.
    /// Automatically derives RootElements and ActiveProcess.
    /// </summary>
    public static StrategyContext FromAttachments(IReadOnlyList<ChatAttachment> attachments)
    {
        var visualElements = attachments
            .AsValueEnumerable()
            .OfType<VisualElementAttachment>()
            .Where(attachment => attachment is { Element: not null, InitialQuery: not null })
            .ToArray();

        var activeProcess = DeriveActiveProcess(visualElements);

        return new StrategyContext
        {
            Attachments = attachments,
            ActiveProcess = activeProcess
        };
    }

    /// <summary>
    /// Derives active process info from visual elements.
    /// </summary>
    private static ProcessInfo? DeriveActiveProcess(IReadOnlyList<VisualElementAttachment> attachments)
    {
        // Find the first element with a valid process ID
        foreach (var attachment in attachments.AsValueEnumerable())
        {
            var processId = attachment.InitialQuery?.Snapshot.ProcessId.GetValueOrDefault(-1) ?? -1;
            if (processId <= 0)
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                return new ProcessInfo(
                    processId,
                    process.ProcessName,
                    process.MainModule?.FileName,
                    process.MainWindowTitle
                );
            }
            catch
            {
                // Process may have exited, continue to next element
            }
        }

        return null;
    }
}

/// <summary>
/// Process information for strategy matching.
/// </summary>
/// <param name="ProcessId">The process ID.</param>
/// <param name="ProcessName">The process name (e.g., "chrome", "code").</param>
/// <param name="ExecutablePath">Full path to the executable, if available.</param>
/// <param name="MainWindowTitle">The main window title, if available.</param>
public record ProcessInfo(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? MainWindowTitle
);
