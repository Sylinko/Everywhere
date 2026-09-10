using System.Diagnostics;
using Everywhere.Automation;
using Everywhere.Mac.Automation;
using Everywhere.Mac.Interop;

namespace Everywhere.Automation.Mac.Probes;

public static partial class Program
{
    private static WindowStatesObservation ProbeWindowStates()
    {
        using var provider = StartProvider();
        var errorOutputTask = provider.StandardError.ReadToEndAsync();
        try
        {
            var providerProcessId = ReadProviderProcessId(provider);
            var windowFields = VisualElementFields.Id |
                VisualElementFields.Type |
                VisualElementFields.Name |
                VisualElementFields.ProcessId |
                VisualElementFields.NativeWindowHandle;
            var windowRequest = new VisualElementQueryRequest(windowFields, 0);
            using var backend = new MacVisualElementBackend();
            using var context = new VisualContext();
            using var retention = context.CreateRetention();

            var windowSet = SendProviderCommandAndRead(provider, "prepare-window-set", "WINDOW_SET", 5);
            var mainWindowId = ParseWindowId(windowSet[1], "main");
            var secondaryWindowId = ParseWindowId(windowSet[2], "secondary");
            var tertiaryWindowId = ParseWindowId(windowSet[3], "tertiary");
            var panelWindowId = ParseWindowId(windowSet[4], "panel");
            using var nativeApplication = AXUIElement.ElementFromPid(providerProcessId) ??
                throw new InvalidOperationException("Could not create the window-state AX application.");
            var rawApplicationWindows = ReadRawApplicationWindows(nativeApplication);

            var mainWindow = QueryNativeWindow(backend, retention, mainWindowId, windowRequest);
            var secondaryWindow = QueryNativeWindow(backend, retention, secondaryWindowId, windowRequest);
            var tertiaryWindow = QueryNativeWindow(backend, retention, tertiaryWindowId, windowRequest);
            var panelWindow = WaitForNativeWindow(backend, retention, panelWindowId, windowRequest, rawApplicationWindows);
            Require(rawApplicationWindows.Any(window => window.WindowId == panelWindowId && window.Role == "AXWindow"),
                "The controlled NSPanel was absent from the raw AXApplication.AXWindows result.");
            foreach (var result in new[] { mainWindow, secondaryWindow, tertiaryWindow, panelWindow })
            {
                Require(result.IsSuccess, $"The controlled window {result.Snapshot.NativeWindowHandle} returned a provider failure.");
                Require(result.Snapshot.Type == VisualElementType.TopLevel, $"The controlled window {result.Snapshot.NativeWindowHandle} was not exposed as an AXWindow TopLevel.");
                Require(result.Snapshot.ProcessId == providerProcessId, $"The controlled window {result.Snapshot.NativeWindowHandle} returned a different provider PID.");
            }

            var screen = backend.Query(retention, VisualElementLocator.FromNativeWindow((nint)mainWindowId), VisualElementResolution.Screen, windowRequest) ??
                throw new InvalidOperationException("The controlled main window did not resolve a Screen.");
            var initialOrder = WaitForProviderWindowOrder(
                screen.Element,
                providerProcessId,
                windowRequest,
                order => ContainsAll(order, mainWindowId, secondaryWindowId, tertiaryWindowId, panelWindowId));

            SendProviderCommandAndRead(provider, "order-secondary-front", "ORDERED", 3);
            var secondaryFrontOrder = WaitForProviderWindowOrder(
                screen.Element,
                providerProcessId,
                windowRequest,
                order => IndexOf(order, secondaryWindowId) >= 0 && IndexOf(order, secondaryWindowId) < IndexOf(order, tertiaryWindowId));

            SendProviderCommandAndRead(provider, "order-tertiary-front", "ORDERED", 3);
            var tertiaryFrontOrder = WaitForProviderWindowOrder(
                screen.Element,
                providerProcessId,
                windowRequest,
                order => IndexOf(order, tertiaryWindowId) >= 0 && IndexOf(order, tertiaryWindowId) < IndexOf(order, secondaryWindowId));

            SendProviderCommandAndRead(provider, "hide-secondary", "HIDDEN", 2);
            SendProviderCommandAndRead(provider, "minimize-tertiary", "MINIMIZED", 2);
            var specialStateOrder = WaitForProviderWindowOrder(
                screen.Element,
                providerProcessId,
                windowRequest,
                order => !order.Contains(secondaryWindowId) && !order.Contains(tertiaryWindowId));
            var hiddenWindowParent = ObserveParent(secondaryWindow.Element, windowRequest);
            var minimizedWindowParent = ObserveParent(tertiaryWindow.Element, windowRequest);

            var sheetStatus = SendProviderCommandAndRead(provider, "show-sheet", "SHEET", 3);
            var sheetWindowId = ParseWindowId(sheetStatus[2], "sheet");
            var focusedSheetElement = WaitForFocusedElement(backend, retention, windowRequest, "sheet-value");
            var sheetTopLevel = backend.Query(retention, VisualElementLocator.Focused, VisualElementResolution.TopLevel, windowRequest) ??
                throw new InvalidOperationException("The focused sheet field did not resolve an enclosing AXWindow.");
            Require(sheetTopLevel.Snapshot.NativeWindowHandle == mainWindowId,
                $"The focused sheet field resolved Quartz window {sheetTopLevel.Snapshot.NativeWindowHandle} instead of its enclosing AXWindow {mainWindowId}.");
            Require(ReferenceEquals(sheetTopLevel.Element, mainWindow.Element), "The focused sheet field did not reuse the canonical enclosing AXWindow.");
            var directSheetWindow = backend.Query(
                retention,
                VisualElementLocator.FromNativeWindow((nint)sheetWindowId),
                VisualElementResolution.Direct,
                windowRequest);

            SendProviderCommandAndRead(provider, "close-secondary", "CLOSED_SECONDARY", 2);
            var destroyedQuery = ObserveDestroyedElement(secondaryWindow.Element, windowRequest);
            var isClosedWindowUnresolvable = WaitForNativeWindowToDisappear(backend, context, secondaryWindowId, windowRequest);
            Require(isClosedWindowUnresolvable, "A closed native window remained resolvable through a new NativeWindow query.");

            return new WindowStatesObservation(
                ProviderProcessId: providerProcessId,
                MainWindowId: mainWindowId,
                SecondaryWindowId: secondaryWindowId,
                TertiaryWindowId: tertiaryWindowId,
                PanelWindowId: panelWindowId,
                SheetWindowId: sheetWindowId,
                InitialProviderOrder: initialOrder,
                SecondaryFrontProviderOrder: secondaryFrontOrder,
                TertiaryFrontProviderOrder: tertiaryFrontOrder,
                HiddenAndMinimizedProviderOrder: specialStateOrder,
                RawApplicationWindows: rawApplicationWindows,
                IsPanelDirectlyResolvable: true,
                PanelType: panelWindow.Snapshot.Type,
                HiddenWindowParent: hiddenWindowParent,
                MinimizedWindowParent: minimizedWindowParent,
                FocusedSheetElementType: focusedSheetElement.Snapshot.Type,
                SheetTopLevelWindowId: (uint)sheetTopLevel.Snapshot.NativeWindowHandle.GetValueOrDefault(),
                IsSheetNativeWindowDirectlyResolvable: directSheetWindow is not null,
                DestroyedElementObservation: destroyedQuery,
                IsClosedWindowUnresolvable: isClosedWindowUnresolvable);
        }
        finally
        {
            StopProvider(provider);
            WaitForDiagnosticOutput(errorOutputTask);
        }
    }

    private static VisualElementQueryResult QueryNativeWindow(
        MacVisualElementBackend backend,
        VisualElementRetention retention,
        uint windowId,
        VisualElementQueryRequest request) => backend.Query(
            retention,
            VisualElementLocator.FromNativeWindow((nint)windowId),
            VisualElementResolution.Direct,
        request) ?? throw new InvalidOperationException($"Could not resolve controlled native window {windowId}.");

    private static VisualElementQueryResult WaitForNativeWindow(
        MacVisualElementBackend backend,
        VisualElementRetention retention,
        uint windowId,
        VisualElementQueryRequest request,
        IReadOnlyList<RawAXWindowObservation> rawApplicationWindows)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 3.0);
        do
        {
            var result = backend.Query(
                retention,
                VisualElementLocator.FromNativeWindow((nint)windowId),
                VisualElementResolution.Direct,
                request);
            if (result is not null)
            {
                return result;
            }

            Thread.Sleep(25);
        } while (Stopwatch.GetTimestamp() < deadline);

        var rawWindows = string.Join(", ", rawApplicationWindows.Select(window =>
            $"{window.WindowId}:{window.WindowIdError}:{window.Role}:{window.RoleError}:{window.Title}"));
        throw new InvalidOperationException($"Could not resolve controlled native window {windowId}. Raw AXWindows: [{rawWindows}].");
    }

    private static VisualElementQueryResult WaitForFocusedElement(
        MacVisualElementBackend backend,
        VisualElementRetention retention,
        VisualElementQueryRequest request,
        string expectedText)
    {
        var textRequest = new VisualElementQueryRequest(request.RequestedFields | VisualElementFields.Text, 256);
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 3.0);
        do
        {
            var result = backend.Query(retention, VisualElementLocator.Focused, VisualElementResolution.Direct, textRequest);
            if (result?.Snapshot.TextPreview == expectedText)
            {
                return result;
            }

            Thread.Sleep(25);
        } while (Stopwatch.GetTimestamp() < deadline);

        throw new InvalidOperationException($"The provider did not expose the expected focused element value '{expectedText}'.");
    }

    private static IReadOnlyList<uint> WaitForProviderWindowOrder(
        VisualElement screen,
        int providerProcessId,
        VisualElementQueryRequest request,
        Func<IReadOnlyList<uint>, bool> predicate)
    {
        IReadOnlyList<uint> order = [];
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 3.0);
        do
        {
            order = ReadProviderWindowOrder(screen, providerProcessId, request);
            if (predicate(order))
            {
                return order;
            }

            Thread.Sleep(25);
        } while (Stopwatch.GetTimestamp() < deadline);

        throw new InvalidOperationException($"The expected provider window order was not observed. Last order: {string.Join(", ", order)}.");
    }

    private static IReadOnlyList<uint> ReadProviderWindowOrder(
        VisualElement screen,
        int providerProcessId,
        VisualElementQueryRequest request)
    {
        var result = new List<uint>();
        using var children = screen.CreateEnumerator(VisualElementRelation.Child, request);
        while (children.MoveNext())
        {
            var snapshot = children.Current.Snapshot;
            if (snapshot.ProcessId == providerProcessId && snapshot.NativeWindowHandle is > 0 and var windowId)
            {
                result.Add(checked((uint)windowId));
            }
        }

        return result;
    }

    private static RelationObservation ObserveParent(VisualElement element, VisualElementQueryRequest request)
    {
        try
        {
            using var parent = element.CreateEnumerator(VisualElementRelation.Parent, request);
            var count = parent.Count;
            var hasMore = parent.HasMore;
            var didMoveNext = parent.MoveNext();
            return new RelationObservation(false, null, count, hasMore, didMoveNext);
        }
        catch (Exception exception)
        {
            return new RelationObservation(true, exception.GetType().FullName, null, null, null);
        }
    }

    private static IReadOnlyList<RawAXWindowObservation> ReadRawApplicationWindows(AXUIElement application)
    {
        var countError = GetAttributeValueCountForContent(application, AXAttributeConstants.Windows, out var count);
        Require(countError == AXError.Success, $"Counting raw AX application windows returned {countError}.");
        var copyError = CopyAttributeValuesForContent(application, AXAttributeConstants.Windows, 0, count, out var values);
        var requiredValues = values ?? throw new InvalidOperationException($"Copying raw AX application windows returned {copyError}.");
        using (requiredValues)
        {
            Require(copyError == AXError.Success, $"Copying raw AX application windows returned {copyError}.");
            var result = new List<RawAXWindowObservation>((int)requiredValues.Count);
            for (nuint index = 0; index < requiredValues.Count; index++)
            {
                using var window = CreateAXElementFromArray(requiredValues, index);
                var idError = GetNativeWindowHandleForContent(window, out var windowId);
                var roleError = CopyStringAttributeForContent(window, AXAttributeConstants.Role, out var role);
                var titleError = CopyStringAttributeForContent(window, AXAttributeConstants.Title, out var title);
                result.Add(new RawAXWindowObservation(windowId, idError, role, roleError, title, titleError));
            }

            return result;
        }
    }

    private static DestroyedElementObservation ObserveDestroyedElement(VisualElement element, VisualElementQueryRequest request)
    {
        try
        {
            var result = element.Query(request);
            return new DestroyedElementObservation(
                DidThrow: false,
                ExceptionType: null,
                FailureKind: result.Failure?.Kind,
                NativeError: (result.Failure?.Exception as AXException)?.Error,
                AvailableFields: result.AvailableFields);
        }
        catch (Exception exception)
        {
            return new DestroyedElementObservation(true, exception.GetType().FullName, null, null, VisualElementFields.None);
        }
    }

    private static bool WaitForNativeWindowToDisappear(
        MacVisualElementBackend backend,
        VisualContext context,
        uint windowId,
        VisualElementQueryRequest request)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 3.0);
        do
        {
            using var retention = context.CreateRetention();
            try
            {
                if (backend.Query(retention, VisualElementLocator.FromNativeWindow((nint)windowId), VisualElementResolution.Direct, request) is null)
                {
                    return true;
                }
            }
            catch (InvalidOperationException exception) when (exception.InnerException is AXException { Error: AXError.InvalidUIElement })
            {
                return true;
            }

            Thread.Sleep(25);
        } while (Stopwatch.GetTimestamp() < deadline);

        return false;
    }

    private static string[] SendProviderCommandAndRead(Process provider, string command, string expectedStatus, int fieldCount)
    {
        SendProviderCommand(provider, command);
        var status = ReadProviderStatus(provider, expectedStatus);
        var fields = status.Split('\t');
        if (fields.Length != fieldCount)
        {
            throw new InvalidOperationException($"Provider status '{status}' did not contain {fieldCount} fields.");
        }

        return fields;
    }

    private static uint ParseWindowId(string value, string name) =>
        uint.TryParse(value, out var windowId) && windowId > 0 ?
            windowId :
            throw new InvalidOperationException($"The provider returned an invalid {name} window ID: '{value}'.");

    private static bool ContainsAll(IReadOnlyList<uint> values, params uint[] expected) => expected.All(values.Contains);

    private static int IndexOf(IReadOnlyList<uint> values, uint value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] == value)
            {
                return index;
            }
        }

        return -1;
    }

    private sealed record WindowStatesObservation(
        int ProviderProcessId,
        uint MainWindowId,
        uint SecondaryWindowId,
        uint TertiaryWindowId,
        uint PanelWindowId,
        uint SheetWindowId,
        IReadOnlyList<uint> InitialProviderOrder,
        IReadOnlyList<uint> SecondaryFrontProviderOrder,
        IReadOnlyList<uint> TertiaryFrontProviderOrder,
        IReadOnlyList<uint> HiddenAndMinimizedProviderOrder,
        IReadOnlyList<RawAXWindowObservation> RawApplicationWindows,
        bool IsPanelDirectlyResolvable,
        VisualElementType? PanelType,
        RelationObservation HiddenWindowParent,
        RelationObservation MinimizedWindowParent,
        VisualElementType? FocusedSheetElementType,
        uint SheetTopLevelWindowId,
        bool IsSheetNativeWindowDirectlyResolvable,
        DestroyedElementObservation DestroyedElementObservation,
        bool IsClosedWindowUnresolvable);

    private sealed record DestroyedElementObservation(
        bool DidThrow,
        string? ExceptionType,
        VisualElementQueryFailureKind? FailureKind,
        AXError? NativeError,
        VisualElementFields AvailableFields);

    private sealed record RelationObservation(
        bool DidThrow,
        string? ExceptionType,
        int? Count,
        bool? HasMore,
        bool? DidMoveNext);

    private sealed record RawAXWindowObservation(
        uint WindowId,
        AXError WindowIdError,
        string? Role,
        AXError RoleError,
        string? Title,
        AXError TitleError);
}
