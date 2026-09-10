using Everywhere.Automation;
using Everywhere.Mac.Automation;

namespace Everywhere.Automation.Mac.Probes;

public static partial class Program
{
    private static ProviderFailureSurveyObservation ProbeProviderFailureSurvey()
    {
        var limits = new VisualContextSnapshotLimits
        {
            MaximumElapsed = TimeSpan.FromSeconds(60),
            MaximumPlatformOperations = 20_000,
            MaximumNodes = 4_096,
            MaximumChildrenPerNode = 256,
            MaximumTextCharactersPerNode = 256,
            MaximumTotalTextCharacters = 1_048_576,
            MaximumProviderFailures = 4_096,
        };
        return RunProviderFailureSurvey(limits);
    }

    private static ProviderFailureSurveyObservation ProbeProviderFailureDefaultSurvey() =>
        RunProviderFailureSurvey(VisualContextSnapshotLimits.Default);

    private static ProviderFailureSurveyObservation RunProviderFailureSurvey(VisualContextSnapshotLimits limits)
    {
        using var backend = new MacVisualElementBackend();
        using var context = new VisualContext();
        using var seedRetention = context.CreateRetention();
        var seedRequest = new VisualElementQueryRequest(VisualElementFields.Id | VisualElementFields.Type, 0);
        var screen = backend.Query(seedRetention, VisualElementLocator.Default, VisualElementResolution.Screen, seedRequest) ??
            throw new InvalidOperationException("The primary Screen element was unavailable.");

        using var snapshot = VisualContextSnapshotter.CreateSnapshot(
            context,
            [screen.Element],
            limits,
            VisualContextTraverseDirections.Child);
        var nodes = new Queue<VisualContextSnapshotNode>(snapshot.Roots);
        var statusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var providerFailureExamples = new List<ProviderFailureNodeObservation>();
        var nodeCount = 0;
        while (nodes.TryDequeue(out var node))
        {
            nodeCount++;
            foreach (var status in node.Status)
            {
                statusCounts[status] = statusCounts.GetValueOrDefault(status) + 1;
                if (status == "Element query failed in the platform provider" && providerFailureExamples.Count < 40)
                {
                    providerFailureExamples.Add(new ProviderFailureNodeObservation(
                        node.Element.Id,
                        node.Snapshot.Type,
                        node.Snapshot.Name,
                        node.AvailableFields,
                        node.MissingFields,
                        node.Children.Count));
                }
            }

            foreach (var child in node.Children)
            {
                nodes.Enqueue(child);
            }
        }

        return new ProviderFailureSurveyObservation(
            nodeCount,
            statusCounts,
            snapshot.Status,
            providerFailureExamples);
    }

    private sealed record ProviderFailureSurveyObservation(
        int NodeCount,
        IReadOnlyDictionary<string, int> NodeStatusCounts,
        IReadOnlyList<string> SnapshotStatus,
        IReadOnlyList<ProviderFailureNodeObservation> ProviderFailureExamples);

    private sealed record ProviderFailureNodeObservation(
        string Id,
        VisualElementType? Type,
        string? Name,
        VisualElementFields AvailableFields,
        VisualElementFields MissingFields,
        int ChildCount);
}
