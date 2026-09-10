using System.Diagnostics;
using Everywhere.Automation;
using Everywhere.Mac.Automation;
using Everywhere.Mac.Interop;

namespace Everywhere.Automation.Mac.Probes;

public static partial class Program
{
    private static QuerySemanticsObservation ProbeQuerySemantics()
    {
        using var provider = StartProvider();
        var errorOutputTask = provider.StandardError.ReadToEndAsync();
        try
        {
            var providerProcessId = ReadProviderProcessId(provider);
            using var backend = new MacVisualElementBackend();
            using var context = new VisualContext();
            using var retention = context.CreateRetention();
            using var nativeApplication = AXUIElement.ElementFromPid(providerProcessId) ??
                throw new InvalidOperationException("Could not create the query-semantics AX application.");
            using var nativeFocusedElement = GetAttributeAsElement(nativeApplication, AXAttributeConstants.FocusedUIElement, out var focusedError) ??
                throw new InvalidOperationException($"Could not copy the query-semantics focused element: {focusedError}.");
            using var nativeWindow = GetAttributeAsElement(nativeFocusedElement, AXAttributeConstants.Window, out var windowError) ??
                throw new InvalidOperationException($"Could not copy the query-semantics AXWindow: {windowError}.");
            var nativeWindowError = GetNativeWindowHandleForContent(nativeWindow, out var nativeWindowId);
            Require(nativeWindowError == AXError.Success && nativeWindowId > 0,
                $"_AXUIElementGetWindow did not map the controlled AXWindow to a Quartz window ID: {nativeWindowError}.");
            var nativeWindowHandle = (nint)nativeWindowId;

            var windowRequest = new VisualElementQueryRequest(
                VisualElementFields.Id | VisualElementFields.Type | VisualElementFields.ProcessId | VisualElementFields.NativeWindowHandle,
                0);
            var screenRequest = new VisualElementQueryRequest(
                VisualElementFields.Id | VisualElementFields.Type | VisualElementFields.Bounds,
                0);
            var identityRequest = new VisualElementQueryRequest(VisualElementFields.Id | VisualElementFields.Type, 0);
            var enumerationRequest = windowRequest;
            var windowLocator = VisualElementLocator.FromNativeWindow(nativeWindowHandle);

            var directWindow = backend.Query(retention, windowLocator, VisualElementResolution.Direct, windowRequest) ??
                throw new InvalidOperationException("NativeWindow + Direct did not resolve the controlled AXWindow.");
            var topLevelWindow = backend.Query(retention, windowLocator, VisualElementResolution.TopLevel, windowRequest) ??
                throw new InvalidOperationException("NativeWindow + TopLevel did not resolve the controlled AXWindow.");
            var containingScreen = backend.Query(retention, windowLocator, VisualElementResolution.Screen, screenRequest) ??
                throw new InvalidOperationException("NativeWindow + Screen did not resolve the controlled AXWindow's display.");

            Require(directWindow.IsSuccess && topLevelWindow.IsSuccess && containingScreen.IsSuccess, "A controlled NativeWindow query returned a provider failure.");
            Require(directWindow.Snapshot.Type == VisualElementType.TopLevel, $"NativeWindow + Direct returned {directWindow.Snapshot.Type} instead of TopLevel.");
            Require(topLevelWindow.Snapshot.Type == VisualElementType.TopLevel, $"NativeWindow + TopLevel returned {topLevelWindow.Snapshot.Type} instead of TopLevel.");
            Require(containingScreen.Snapshot.Type == VisualElementType.Screen, $"NativeWindow + Screen returned {containingScreen.Snapshot.Type} instead of Screen.");
            Require(ReferenceEquals(directWindow.Element, topLevelWindow.Element), "Direct and TopLevel NativeWindow queries did not reuse the canonical AXWindow.");
            Require(directWindow.Snapshot.Id == topLevelWindow.Snapshot.Id, "Direct and TopLevel NativeWindow queries returned different AXWindow IDs.");
            Require(directWindow.Snapshot.NativeWindowHandle == nativeWindowHandle, "The controlled AXWindow query returned a different Quartz window ID.");
            Require(directWindow.Snapshot.ProcessId == providerProcessId, "The controlled AXWindow query returned a different provider PID.");

            VisualElementQueryResult? projectedParent = null;
            var parentCount = 0;
            var parentDeadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 3.0);
            do
            {
                using var parent = directWindow.Element.CreateEnumerator(VisualElementRelation.Parent, screenRequest);
                parentCount = parent.Count;
                if (parentCount == 1 && parent.HasMore && parent.MoveNext())
                {
                    projectedParent = parent.Current;
                    Require(!parent.HasMore && !parent.MoveNext(), "The AXWindow enumerated more than one projected Screen parent.");
                    break;
                }

                Thread.Sleep(25);
            } while (Stopwatch.GetTimestamp() < parentDeadline);

            var requiredProjectedParent = projectedParent ??
                throw new InvalidOperationException($"The on-screen AXWindow reported {parentCount} projected Screen parents.");
            Require(ReferenceEquals(requiredProjectedParent.Element, containingScreen.Element), "AXWindow.Parent and NativeWindow + Screen did not reuse the canonical Screen.");

            using (var children = directWindow.Element.CreateEnumerator(VisualElementRelation.Child, identityRequest))
            {
                Require(children.HasMore && children.MoveNext(), "The controlled AXWindow did not expose its native AX descendants.");
                Require(children.Current.Snapshot.Type != VisualElementType.TopLevel, "An AXWindow child was incorrectly projected as another top-level element.");
            }

            var descendantWindowHandleRequest = new VisualElementQueryRequest(VisualElementFields.Type | VisualElementFields.NativeWindowHandle, 0);
            using (var children = directWindow.Element.CreateEnumerator(VisualElementRelation.Child, descendantWindowHandleRequest))
            {
                Require(children.HasMore && children.MoveNext(), "The controlled AXWindow did not expose a descendant for NativeWindowHandle validation.");
                var descendant = children.Current;
                Require(descendant.IsSuccess, "A non-window AX descendant returned a provider failure while declining NativeWindowHandle.");
                Require(descendant.Snapshot.Type != VisualElementType.TopLevel, "The NativeWindowHandle descendant check unexpectedly returned an AXWindow.");
                Require(descendant.Snapshot.NativeWindowHandle is null, "A non-window AX descendant inherited its enclosing Quartz window identifier.");
                Require(!descendant.AvailableFields.HasFlag(VisualElementFields.NativeWindowHandle), "A non-window AX descendant advertised NativeWindowHandle as available.");
                Require(descendant.MissingFields.HasFlag(VisualElementFields.NativeWindowHandle), "A non-window AX descendant did not report NativeWindowHandle as unavailable.");
            }

            var didScreenContainWindow = false;
            var observedScreenWindowCount = 0;
            using (var children = containingScreen.Element.CreateEnumerator(VisualElementRelation.Child, enumerationRequest))
            {
                while (children.HasMore)
                {
                    Require(children.MoveNext(), "Screen.Children reported lookahead but did not advance.");
                    observedScreenWindowCount++;
                    Require(children.Current.Snapshot.Type == VisualElementType.TopLevel, "Screen.Children returned a non-AXWindow element.");
                    if (children.Current.Snapshot.NativeWindowHandle != nativeWindowHandle)
                    {
                        continue;
                    }

                    didScreenContainWindow = true;
                    Require(ReferenceEquals(children.Current.Element, directWindow.Element), "Screen.Children did not reuse the canonical controlled AXWindow.");
                }
            }

            Require(didScreenContainWindow, "The controlled AXWindow was absent from its assigned Screen.Children Z-order projection.");

            var applicationElement = GetOrCreateAXElement(backend, retention, nativeApplication);
            var applicationResult = applicationElement.Query(identityRequest);
            Require(applicationResult.Snapshot.Type == VisualElementType.Unknown, "AXApplication was exposed as a visual TopLevel.");
            Require(HasNoRelations(applicationElement, identityRequest), "AXApplication leaked into the projected Parent/Child graph.");

            var systemWideResult = backend.Query(retention, VisualElementLocator.Default, VisualElementResolution.Direct, identityRequest) ??
                throw new InvalidOperationException("Default + Direct did not return the AXSystemWide special root.");
            Require(systemWideResult.IsSuccess, "Default + Direct could not query the AXSystemWide special root locally.");
            Require(systemWideResult.Snapshot.Type == VisualElementType.Unknown, "AXSystemWide was exposed as a visual TopLevel.");
            Require(HasNoRelations(systemWideResult.Element, identityRequest), "AXSystemWide was traversable instead of isolated.");

            var defaultTopLevel = backend.Query(retention, VisualElementLocator.Default, VisualElementResolution.TopLevel, identityRequest) ??
                throw new InvalidOperationException("Default + TopLevel did not resolve the first eligible Quartz Z-order window.");
            Require(defaultTopLevel.Snapshot.Type == VisualElementType.TopLevel, "Default + TopLevel did not return an AXWindow.");
            Require(!ReferenceEquals(systemWideResult.Element, defaultTopLevel.Element), "Default + TopLevel reused the AXSystemWide special root.");

            return new QuerySemanticsObservation(
                ProviderProcessId: providerProcessId,
                NativeWindowId: (uint)nativeWindowHandle,
                DirectWindowId: directWindow.Element.Id,
                TopLevelWindowId: topLevelWindow.Element.Id,
                ContainingScreenId: containingScreen.Element.Id,
                AreDirectAndTopLevelCanonical: ReferenceEquals(directWindow.Element, topLevelWindow.Element),
                IsWindowParentCanonicalScreen: true,
                ObservedScreenWindowCount: observedScreenWindowCount,
                DidScreenContainControlledWindow: didScreenContainWindow,
                ApplicationType: applicationResult.Snapshot.Type,
                IsApplicationIsolated: true,
                SystemWideType: systemWideResult.Snapshot.Type,
                IsSystemWideIsolated: true,
                DefaultTopLevelType: defaultTopLevel.Snapshot.Type);
        }
        finally
        {
            StopProvider(provider);
            WaitForDiagnosticOutput(errorOutputTask);
        }
    }

    private static bool HasNoRelations(VisualElement element, VisualElementQueryRequest request)
    {
        foreach (var relation in Enum.GetValues<VisualElementRelation>())
        {
            using var enumerator = element.CreateEnumerator(relation, request);
            if (enumerator.Count != 0 || enumerator.HasMore || enumerator.MoveNext())
            {
                return false;
            }
        }

        return true;
    }

    private sealed record QuerySemanticsObservation(
        int ProviderProcessId,
        uint NativeWindowId,
        string DirectWindowId,
        string TopLevelWindowId,
        string ContainingScreenId,
        bool AreDirectAndTopLevelCanonical,
        bool IsWindowParentCanonicalScreen,
        int ObservedScreenWindowCount,
        bool DidScreenContainControlledWindow,
        VisualElementType? ApplicationType,
        bool IsApplicationIsolated,
        VisualElementType? SystemWideType,
        bool IsSystemWideIsolated,
        VisualElementType? DefaultTopLevelType);
}
