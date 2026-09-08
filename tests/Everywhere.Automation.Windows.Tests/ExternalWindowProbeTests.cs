using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Everywhere.Automation.WebView.Probe;
using Everywhere.Chat;
using Everywhere.Windows.Automation;
using Everywhere.Windows.Interop;
using Everywhere.Windows.Interop.UIAutomation;

namespace Everywhere.Automation.Windows.Tests;

/// <summary>Read-only observations of an explicitly selected running application; never owns or closes that application.</summary>
public sealed class ExternalWindowProbeTests
{
    [Test]
    [Explicit("Reads the selected running application's visible windows and saves local accessibility artifacts.")]
    [Platform("Win")]
    public void Observe_WhenExternalWindowHasMalformedRelations_ReturnsBoundedProjectionAndReleasesTransientIdentities()
    {
        var processName = Environment.GetEnvironmentVariable("EVERYWHERE_EXTERNAL_PROBE_PROCESS") ?? "Reqable";
        var processIds = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process) processIds.Add((uint)process.Id);
        }
        var windows = new List<nint>();
        var inventory = new List<object>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (!processIds.Contains(pid)) return true;
            var isVisible = IsWindowVisible(window);
            inventory.Add(new { pid, window = $"0x{window:X}", isVisible });
            if (isVisible) windows.Add(window);
            return true;
        }, 0);
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(inventory));
        Assert.That(windows, Is.Not.Empty, "Open the selected application's window before running this explicit probe.");
        var output = Environment.GetEnvironmentVariable("EVERYWHERE_EXTERNAL_PROBE_OUTPUT") ?? Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "external-window");
        Directory.CreateDirectory(output);
        using var backend = new WindowsVisualElementBackend();
        var measurements = new List<object>();
        foreach (var window in windows.Take(3))
        {
            using var context = new VisualContext();
            using var acquisition = context.CreateRetention();
            var root = backend.Query(acquisition, VisualElementLocator.FromNativeWindow(window)) ?? throw new InvalidOperationException("The target window could not be acquired.");
            var baseline = CountIdentities(context);
            for (var pass = 0; pass < 3; pass++)
            {
                using var turn = context.BeginTurn();
                var stopwatch = Stopwatch.StartNew();
                var limits = new VisualContextSnapshotLimits { MaximumNodes = 128, MaximumChildrenPerNode = 32, MaximumPlatformOperations = 512, MaximumElapsed = TimeSpan.FromSeconds(5) };
                using (var snapshot = VisualContextSnapshotter.CreateSnapshot(context, [root.Element], limits, VisualContextTraverseDirections.Child))
                {
                    var pending = new Stack<VisualContextSnapshotNode>(snapshot.Roots);
                    var nodes = new List<VisualContextSnapshotNode>();
                    var ids = new HashSet<string>();
                    while (pending.TryPop(out var node))
                    {
                        Assert.That(ids.Add(node.Element.Id), Is.True, "The projected forest repeats an identity or contains a cycle.");
                        nodes.Add(node);
                        foreach (var child in node.Children) pending.Push(child);
                    }
                    var prompt = VisualContextPromptBuilder.Build(context, snapshot).ToString();
                    File.WriteAllText(Path.Combine(output, $"{window:X}-{pass}.txt"), prompt);
                    var targetId = Enumerable.Range(1, context.NextTargetId - 1).First(id => context.TryGetTarget(id, out var target) && target is ElementTarget);
                    Assert.That(context.TryGetTarget(targetId, out var target), Is.True);
                    if (target is not ElementTarget elementTarget) throw new InvalidOperationException("Expected an element target.");
                    var followup = elementTarget.Element.Query(VisualElementQueryRequest.Default);
                    measurements.Add(new { window = $"0x{window:X}", pass, elapsed = stopwatch.Elapsed, nodeCount = nodes.Count, distinctIds = ids.Count, emptyNodes = nodes.Count(node => string.IsNullOrEmpty(node.Snapshot.Name) && string.IsNullOrEmpty(node.Snapshot.TextPreview)), rootStatus = snapshot.Status, localStatus = nodes.SelectMany(node => node.Status).ToArray(), targets = turn.Count, followupFailure = followup.Failure?.Kind.ToString() });
                    Assert.That(nodes.Count, Is.LessThanOrEqualTo(limits.MaximumNodes));
                }
                turn.Complete();
                context.TrimRetainedTurns(0);
                var remaining = CountIdentities(context);
                measurements.Add(new { window = $"0x{window:X}", pass, baseline, remaining, targetCount = context.TargetCount });
                Assert.That(remaining, Is.EqualTo(baseline), "An operation left canonical identities retained after turn eviction.");
                Assert.That(context.TargetCount, Is.Zero);
            }
            measurements.Add(new { window = $"0x{window:X}", runtimeIdComparison = CompareRuntimeIds(window) });
            File.WriteAllText(Path.Combine(output, $"{window:X}-native.json"), JsonSerializer.Serialize(TopologyProbe.Capture(window)));
        }
        File.WriteAllText(Path.Combine(output, "measurements.json"), JsonSerializer.Serialize(measurements));
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(measurements));
    }

    private static int CountIdentities(VisualContext context)
    {
        var maps = (IDictionary)(typeof(VisualContext).GetField("_identityMaps", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(context) ?? throw new MissingFieldException());
        var count = 0;
        foreach (var map in maps.Values)
        {
            if (map is null) continue;
            var entries = (IDictionary)(map.GetType().GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(map) ?? throw new MissingFieldException());
            count += entries.Count;
        }
        return count;
    }

    private static IReadOnlyList<object> CompareRuntimeIds(nint window)
    {
        using var client = UIAutomationClient.Create();
        client.ConfigureTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        using var walker = client.CreateContentViewWalker();
        using var cache = client.CreateCacheRequest(UIAutomationCacheOptions.RuntimeId);
        var current = client.ElementFromHandleBuildCache(window, cache);
        var observations = new List<object>();
        try
        {
            for (var depth = 0; depth < 4 && current.HasValue; depth++)
            {
                string? cachedId = null;
                try { cachedId = current.ReadCachedRuntimeId(0, static (id, _) => string.Join(".", id.ToArray())); }
                catch (InvalidOperationException) { }
                using var reference = current.Realize();
                var rcw = Marshal.GetObjectForIUnknown(GetPointer(reference));
                try
                {
                    var directId = ((global::Interop.UIAutomationClient.IUIAutomationElement)rcw).GetRuntimeId();
                    observations.Add(new { depth, cachedId, directId });
                }
                finally { Marshal.ReleaseComObject(rcw); }
                var next = walker.NavigateBuildCache(in current, UIAutomationNavigationDirection.FirstChild, cache);
                current.Dispose();
                current = next;
            }
        }
        finally { current.Dispose(); }
        return observations;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_pointer")]
    private static extern ref nint GetPointer(ComReference reference);

    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);
}
