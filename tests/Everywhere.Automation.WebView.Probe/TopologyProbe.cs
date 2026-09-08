using System.Diagnostics;
using System.Runtime.CompilerServices;
using Everywhere.Windows.Interop;
using Everywhere.Windows.Interop.UIAutomation;

namespace Everywhere.Automation.WebView.Probe;

/// <summary>Records native edges before canonicalization, retaining original COM references for duplicate-parent comparison.</summary>
public static class TopologyProbe
{
    /// <summary>Samples a bounded Content View twice. Pointer values identify observations, not logical elements.</summary>
    public static IReadOnlyList<object> Capture(nint windowHandle)
    {
        using var client = UIAutomationClient.Create();
        client.ConfigureTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        using var walker = client.CreateContentViewWalker();
        using var cache = client.CreateCacheRequest(UIAutomationCacheOptions.RuntimeId | UIAutomationCacheOptions.ControlType | UIAutomationCacheOptions.Name);
        var records = new List<object>();
        var stopwatch = Stopwatch.StartNew();
        for (var pass = 0; pass < 2 && stopwatch.Elapsed < TimeSpan.FromSeconds(30); pass++)
        {
            var owners = new List<UIAutomationElementReference>();
            var observed = new Dictionary<string, (string? Parent, UIAutomationElementReference Reference)>();
            var pending = new Queue<(string? Id, UIAutomationElementReference Reference)>();
            try
            {
                using var root = client.ElementFromHandleBuildCache(windowHandle, cache);
                var rootId = GetId(in root) ?? throw new InvalidOperationException("The diagnostic root has no usable cached RuntimeId.");
                var rootReference = root.Realize();
                owners.Add(rootReference);
                observed.Add(rootId, (null, rootReference));
                pending.Enqueue((rootId, rootReference));
                var edgeCount = 0;
                while (pending.TryDequeue(out var parent) && edgeCount < 256 && stopwatch.Elapsed < TimeSpan.FromSeconds(30))
                {
                    var child = walker.NavigateBuildCache(parent.Reference, UIAutomationNavigationDirection.FirstChild, cache);
                    try
                    {
                        while (child.HasValue && edgeCount < 256 && stopwatch.Elapsed < TimeSpan.FromSeconds(30))
                        {
                            var id = GetId(in child);
                            var reference = child.Realize();
                            owners.Add(reference);
                            var previous = default((string? Parent, UIAutomationElementReference Reference));
                            var hasPrevious = id is not null && observed.TryGetValue(id, out previous);
                            using var nativeParent = walker.NavigateBuildCache(reference, UIAutomationNavigationDirection.Parent, cache);
                            var actualParent = nativeParent.HasValue ? GetId(in nativeParent) : null;
                            string? previousActualParent = null;
                            if (hasPrevious && previous.Reference is { } previousReference)
                            {
                                using var previousParent = walker.NavigateBuildCache(previousReference, UIAutomationNavigationDirection.Parent, cache);
                                previousActualParent = previousParent.HasValue ? GetId(in previousParent) : null;
                            }
                            records.Add(new { pass, ordinal = edgeCount++, parent = parent.Id, parentPointer = $"0x{GetPointer(parent.Reference):X}", child = id, pointer = $"0x{GetPointer(reference):X}", type = child.CachedControlType.ToString(), name = child.GetCachedName(), actualParent, hasPrevious, previousParent = previous.Parent, previousActualParent });
                            // Missing identities remain diagnostic observations, never fabricated canonical targets.
                            if (!hasPrevious && id is not null)
                            {
                                observed.Add(id, (parent.Id, reference));
                            }
                            // Diagnostic-only traversal can inspect unidentified references under the same hard edge cap.
                            // No synthetic identity is published or inserted into the production map.
                            if (!hasPrevious) pending.Enqueue((id, reference));
                            var next = walker.NavigateBuildCache(in child, UIAutomationNavigationDirection.NextSibling, cache);
                            child.Dispose();
                            child = next;
                            // A repeated sibling can otherwise consume the complete diagnostic budget in a cycle.
                            if (hasPrevious && previous.Parent == parent.Id) break;
                        }
                    }
                    finally { child.Dispose(); }
                }
                records.Add(new { pass, edgeCount, elapsed = stopwatch.Elapsed, hasPending = pending.Count > 0, hasReachedEdgeLimit = edgeCount >= 256 });
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                records.Add(new { pass, error = exception.Message, elapsed = stopwatch.Elapsed });
            }
            finally
            {
                foreach (var owner in owners) owner.Dispose();
            }
        }
        return records;
    }

    private static string? GetId(scoped in UIAutomationElement element)
    {
        try { return element.ReadCachedRuntimeId(0, static (id, _) => string.Join(".", id.ToArray())); }
        catch (InvalidOperationException) { return null; }
    }

    // The probe observes private ownership storage without adding a production diagnostic API.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_pointer")]
    private static extern ref nint GetPointer(ComReference reference);
}
