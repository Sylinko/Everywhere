using AppKit;
using Everywhere.Automation;
using Everywhere.Mac.Automation;
using Everywhere.Mac.Interop;
using Foundation;

namespace Everywhere.Automation.Mac.Probes;

public static partial class Program
{
    private static ScreenTopologyNotificationObservation ProbeScreenTopologyNotification()
    {
        var before = CGDisplayTopology.Current;
        using var backend = new MacVisualElementBackend();
        using var context = new VisualContext();
        using var retention = context.CreateRetention();
        var request = new VisualElementQueryRequest(VisualElementFields.Id | VisualElementFields.Type | VisualElementFields.Name | VisualElementFields.Bounds, 0);
        var oldResult = backend.Query(retention, VisualElementLocator.Default, VisualElementResolution.Screen, request) ??
            throw new InvalidOperationException("The topology probe did not resolve the primary Screen.");
        var oldElement = oldResult.Element as ScreenVisualElement ??
            throw new InvalidOperationException("The topology probe did not receive a ScreenVisualElement.");

        NSNotificationCenter.DefaultCenter.PostNotificationName(
            NSApplication.DidChangeScreenParametersNotification.ToString(),
            NSApplication.SharedApplication);

        var after = CGDisplayTopology.Current;
        var staleResult = oldElement.Query(request);
        var newResult = backend.Query(retention, VisualElementLocator.Default, VisualElementResolution.Screen, request) ??
            throw new InvalidOperationException("The topology probe did not resolve the replacement primary Screen.");
        var newElement = newResult.Element as ScreenVisualElement ??
            throw new InvalidOperationException("The topology probe replacement was not a ScreenVisualElement.");

        Require(!ReferenceEquals(before, after), "The screen-parameters notification did not replace the topology snapshot.");
        Require(after.Generation == checked(before.Generation + 1), "The screen-parameters notification did not advance the topology generation exactly once.");
        Require(before.Displays.SequenceEqual(after.Displays), "Posting an unchanged screen-parameters notification changed the captured display values.");
        Require(staleResult.Failure?.Kind == VisualElementQueryFailureKind.ElementUnavailable, "The old-generation Screen element did not report ElementUnavailable.");
        Require(oldElement.TopologyGeneration == before.Generation, "The original Screen element did not retain its topology generation.");
        Require(newElement.TopologyGeneration == after.Generation, "The replacement Screen element did not use the new topology generation.");
        Require(!ReferenceEquals(oldElement, newElement), "The identity map reused a Screen element from the old topology generation.");
        Require(oldElement.Id != newElement.Id, "The backend reused a Screen ID across topology generations.");
        Require(newResult.IsSuccess, "The replacement Screen query did not succeed.");

        return new ScreenTopologyNotificationObservation(
            before.Generation,
            after.Generation,
            before.Displays.Count,
            oldElement.DisplayId,
            oldElement.Id,
            newElement.Id,
            staleResult.Failure?.Kind,
            newResult.IsSuccess);
    }

    private sealed record ScreenTopologyNotificationObservation(
        long PreviousGeneration,
        long CurrentGeneration,
        int DisplayCount,
        uint PrimaryDisplayId,
        string PreviousScreenId,
        string CurrentScreenId,
        VisualElementQueryFailureKind? PreviousScreenFailure,
        bool IsCurrentScreenQuerySuccessful);
}
