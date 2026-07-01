using System.Diagnostics;
using Everywhere.Chat;
using Everywhere.Interop;
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
    /// Root visual elements derived from attachments.
    /// Each element's ancestor chain ends at Screen or TopLevel.
    /// Strategy matching follows paths from these roots downward.
    /// </summary>
    public IReadOnlyList<IVisualElement> RootElements { get; init; } = [];

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
            .Where(a => a.Element?.Target is not null)
            .Select(a => a.Element!.Target!)
            .ToList();

        var rootElements = DeriveRootElements(visualElements);
        var activeProcess = DeriveActiveProcess(visualElements);

        return new StrategyContext
        {
            Attachments = attachments,
            RootElements = rootElements,
            ActiveProcess = activeProcess
        };
    }

    /// <summary>
    /// Derives root elements (Screen or TopLevel) from visual elements.
    /// </summary>
    private static List<IVisualElement> DeriveRootElements(IReadOnlyList<IVisualElement> elements)
    {
        var roots = new HashSet<IVisualElement>(ReferenceEqualityComparer.Instance);

        foreach (var element in elements.AsValueEnumerable())
        {
            var current = element;
            while (current.Parent is { } parent)
            {
                current = parent;
            }

            // current is now the root (Screen or TopLevel with null parent)
            roots.Add(current);
        }

        return roots.ToList();
    }

    /// <summary>
    /// Derives active process info from visual elements.
    /// </summary>
    private static ProcessInfo? DeriveActiveProcess(IReadOnlyList<IVisualElement> elements)
    {
        // Find the first element with a valid process ID
        foreach (var element in elements.AsValueEnumerable())
        {
            var processId = element.ProcessId;
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
