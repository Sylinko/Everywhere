using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Everywhere.Automation;
using Everywhere.Mac.Automation;
using Everywhere.Mac.Interop;
using Foundation;

namespace Everywhere.Automation.Mac.Probes;

public static partial class Program
{
    // Each state change is acknowledged by the provider's AppKit thread before any client observation.
    private static object ProbePanelDiscovery()
    {
        using var provider = StartProvider();
        var errors = provider.StandardError.ReadToEndAsync();
        try
        {
            var pid = ReadProviderProcessId(provider);
            SendProviderCommandAndRead(provider, "prepare-window-set", "WINDOW_SET", 5);
            using var application = AXUIElement.ElementFromPid(pid) ?? throw new InvalidOperationException("No AX application.");
            using var backend = new MacVisualElementBackend();
            using var context = new VisualContext();
            using var retention = context.CreateRetention();
            var observations = new List<object>();
            foreach (var command in new[] { "inspect-panel", "panel-key", "panel-deactivate", "panel-key", "panel-persistent", "panel-deactivate", "panel-key", "panel-floating", "panel-accessible" })
            {
                var status = SendProviderCommandAndRead(provider, command, "PANEL", 2);
                using var document = JsonDocument.Parse(status[1]);
                var state = document.RootElement.Clone();
                var id = state.GetProperty("WindowId").GetUInt32();
                var windows = ObservePanelArray(application, AXAttributeConstants.Windows);
                var children = ObservePanelArray(application, AXAttributeConstants.Children);
                using var focused = GetAttributeAsElement(application, AXAttributeConstants.FocusedWindow, out var focusError);
                using var hit = HitPanel(application, state.GetProperty("X").GetSingle(), state.GetProperty("Y").GetSingle(), out var hitError);
                using var hitWindow = hit is null ? null : GetAttributeAsElement(hit, AXAttributeConstants.Window, out _);
                var resolved = backend.Query(retention, VisualElementLocator.FromNativeWindow((nint)id),
                    VisualElementResolution.Direct, new VisualElementQueryRequest(VisualElementFields.Id | VisualElementFields.Type, 0));
                observations.Add(new
                {
                    State = state, Quartz = ObservePanelQuartz(id), Windows = windows, Children = children,
                    FocusError = focusError, Focus = DescribePanelElement(focused),
                    HitError = hitError, Hit = DescribePanelElement(hit), HitWindow = DescribePanelElement(hitWindow),
                    AreFocusAndHitWindowEqual = focused is not null && hitWindow is not null && focused.Equals(hitWindow),
                    AreFocusAndHitEqual = focused is not null && hit is not null && focused.Equals(hit),
                    IsBackendResolved = resolved is not null,
                });
            }
            return observations;
        }
        finally
        {
            StopProvider(provider);
            WaitForDiagnosticOutput(errors);
        }
    }

    private static object ObservePanelArray(AXUIElement application, NSString attribute)
    {
        var countError = GetAttributeValueCountForContent(application, attribute, out var count);
        if (countError != AXError.Success || count < 0)
        {
            return new { CountError = countError, Count = (long)count, IsCopySkipped = true };
        }
        var copyError = CopyAttributeValuesForContent(application, attribute, 0, Math.Min(count, 64), out var values);
        using (values)
        {
            var elements = new List<object?>();
            if (values is not null)
            {
                for (var index = (nuint)0; index < values.Count; index++)
                {
                    using var element = CreateAXElementFromArray(values, index);
                    elements.Add(DescribePanelElement(element));
                }
            }
            return new { CountError = countError, Count = (long)count, CopyError = copyError, Elements = elements };
        }
    }

    private static object ObservePanelQuartz(uint id)
    {
        // IncludingWindow (8) observes this exact native window, independently of AX discovery.
        var array = CopyPanelQuartzWindowInfo(8, id);
        if (array == 0) return new { IsAvailable = false };
        try
        {
            var description = CFCopyDescription(array);
            try
            {
                return new { IsAvailable = true, Entries = ReadCoreFoundationString(description) };
            }
            finally
            {
                if (description != 0) CFRelease(description);
            }
        }
        finally
        {
            CFRelease(array);
        }
    }

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", EntryPoint = "CGWindowListCopyWindowInfo")]
    private static partial nint CopyPanelQuartzWindowInfo(uint options, uint windowId);

    private static object? DescribePanelElement(AXUIElement? element)
    {
        if (element is null) return null;
        var idError = GetNativeWindowHandleForContent(element, out var id);
        var roleError = CopyStringAttributeForContent(element, AXAttributeConstants.Role, out var role);
        var titleError = CopyStringAttributeForContent(element, AXAttributeConstants.Title, out var title);
        return new { WindowId = id, IdError = idError, Role = role, RoleError = roleError, Title = title, TitleError = titleError };
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ElementAtPosition")]
    private static extern AXUIElement? HitPanel(AXUIElement application, float x, float y, out AXError error);
}
