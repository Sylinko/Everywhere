using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Everywhere.Automation.TestApp;
using Everywhere.Windows.Automation;
using Everywhere.Windows.Interop;
using Everywhere.Windows.Interop.UIAutomation;
using Interop.UIAutomationClient;

namespace Everywhere.Automation.Windows.Tests;

/// <summary>Exercises the production text reader against controlled native multiline editors.</summary>
public sealed class NativeTextPagingTests
{
    [TestCase("winforms")]
    [TestCase("cefsharp")]
    [Explicit("Launches a controlled editor and compares bounded GetText, unbounded GetText, and TextUnit.Character traversal.")]
    [Platform("Win")]
    [Category("UIAutomationProbe")]
    public async Task GetText_WhenTextSizeVaries_ReportsBoundedAndUnboundedCost(string platform)
    {
        var executable = platform switch
        {
            "winforms" => ControlledTestAppPaths.WinForms,
            "avalonia" => ControlledTestAppPaths.Avalonia,
            "cefsharp" => ControlledTestAppPaths.CefSharp,
            _ => throw new ArgumentOutOfRangeException(nameof(platform)),
        };
        var (controller, ready) = await TestAppProcessController.StartAsync(executable, "document-editor", 114514, TimeSpan.FromSeconds(45));
        await using (controller)
        {
            if (platform == "cefsharp") await Task.Delay(TimeSpan.FromMilliseconds(750));
            var rootWindow = (nint)ready.Roots.Single().NativeHandle;
            var queryWindow = platform == "cefsharp"
                ? EnumerateWindowTree(rootWindow).Single(window => GetWindowClass(window) == "Chrome_RenderWidgetHostHWND")
                : rootWindow;
            using var backend = new WindowsVisualElementBackend();
            using var context = new VisualContext();
            using var acquisition = context.CreateRetention();
            var root = backend.Query(acquisition, VisualElementLocator.FromNativeWindow(queryWindow)) ?? throw new InvalidOperationException("Editor root not found.");
            using var snapshot = VisualContextSnapshotter.CreateSnapshot(context, [root.Element], allowedTraverseDirections: VisualContextTraverseDirections.Child);
            var target = platform == "cefsharp"
                ? Flatten(snapshot.Roots).Where(static node => node.Snapshot.Type == VisualElementType.Document)
                    .OrderByDescending(static node => node.Snapshot.TextPreview?.Length ?? 0)
                    .Select(static node => node.Element)
                    .FirstOrDefault(static element => ProbeTextUnitCharacter(element).IsSupported)
                : Flatten(snapshot.Roots).Where(static node => node.Snapshot.Type == VisualElementType.TextEdit)
                    .OrderByDescending(static node => node.Snapshot.TextPreview?.Length ?? 0)
                    .Select(static node => node.Element)
                    .FirstOrDefault(TryInitializeProbeEditor);
            if (target is null) throw new InvalidOperationException($"{platform} exposed no suitable TextPattern element for the controlled probe.");
            var measurements = new List<object>();
            foreach (var sourceLength in new[] { 4_096, 65_536, 262_144, 1_048_576 })
            {
                var sourceText = new string('x', sourceLength);
                await controller.SetTextAsync(sourceText);
                await Task.Delay(platform == "cefsharp" ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromMilliseconds(50));
                var measurement = ProbeTextRetrievalCost(target, sourceLength, shouldMeasureMove: sourceLength <= 262_144);
                measurements.Add(measurement);
                TestContext.Progress.WriteLine(JsonSerializer.Serialize(new { platform, measurement }));
            }

            var output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", $"get-text-cost-{platform}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? throw new InvalidOperationException());
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.Progress.WriteLine(JsonSerializer.Serialize(new { platform, output }));
        }
    }

    [Test]
    [Explicit("Launches a controlled CefSharp document and measures its UIA TextUnit.Character count.")]
    [Platform("Win")]
    [Category("UIAutomationProbe")]
    public async Task TextUnitCharacter_WhenChromiumDocumentIsRead_ReportsLengthAccuracyAndCost()
    {
        var (controller, ready) = await TestAppProcessController.StartAsync(
            ControlledTestAppPaths.CefSharp,
            "document-editor",
            114514,
            TimeSpan.FromSeconds(45));
        await using (controller)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            var rootWindow = (nint)ready.Roots.Single().NativeHandle;
            var windows = EnumerateWindowTree(rootWindow);
            var rendererWindow = windows.Single(window => GetWindowClass(window) == "Chrome_RenderWidgetHostHWND");
            using var backend = new WindowsVisualElementBackend();
            using var context = new VisualContext();
            using var acquisition = context.CreateRetention();
            var root = backend.Query(acquisition, VisualElementLocator.FromNativeWindow(rendererWindow)) ?? throw new InvalidOperationException("Chromium renderer root not found.");
            using var snapshot = VisualContextSnapshotter.CreateSnapshot(context, [root.Element], allowedTraverseDirections: VisualContextTraverseDirections.Child);
            var nodes = Flatten(snapshot.Roots).ToArray();
            var documentMeasurement = nodes
                .Where(static node => node.Snapshot.Type == VisualElementType.Document)
                .Select(static node => ProbeTextUnitCharacter(node.Element))
                .Where(static probe => probe.IsSupported)
                .OrderByDescending(static probe => probe.NativeTextUtf16Length)
                .FirstOrDefault();
            var editor = nodes.Where(static node => node.Snapshot.Type == VisualElementType.TextEdit)
                .Select(static node => node.Element)
                .FirstOrDefault(TryInitializeProbeEditor)
                ?? throw new InvalidOperationException("Chromium exposed no writable text element for the controlled probe.");
            var measurements = new List<object>();
            foreach (var (name, sourceText) in CreateTextShapeCases())
            {
                editor.SetText(sourceText);
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                var observed = editor.ReadText(0, 65_536);
                Assert.That(observed.Failure, Is.Null);
                Assert.That(observed.NextOffset, Is.Null);
                var observedText = observed.Text ?? string.Empty;
                var probe = ProbeTextUnitCharacter(editor);
                measurements.Add(new
                {
                    name,
                    sourceUtf16Length = sourceText.Length,
                    observedUtf16Length = observedText.Length,
                    observedScalarCount = observedText.EnumerateRunes().Count(),
                    observedTextElementCount = new System.Globalization.StringInfo(observedText).LengthInTextElements,
                    probe.IsSupported,
                    probe.NativeTextUtf16Length,
                    probe.NativeTextScalarCount,
                    probe.NativeTextElementCount,
                    probe.MovedUnits,
                    probe.IsRangeEndAligned,
                    probe.MoveCalls,
                    probe.Elapsed,
                    utf16Delta = probe.MovedUnits - observedText.Length,
                    textElementDelta = probe.MovedUnits - new System.Globalization.StringInfo(observedText).LengthInTextElements,
                    probe.Failure,
                });
            }

            var measurement = new { provider = "cefsharp", documentMeasurement, editorMeasurements = measurements };
            var output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "text-unit-character-cefsharp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? throw new InvalidOperationException());
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(measurement, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.Progress.WriteLine(JsonSerializer.Serialize(new { output, measurement }));
        }
    }

    [TestCase("winforms")]
    [TestCase("avalonia")]
    [Explicit("Launches a controlled editor and measures UIA TextUnit.Character against known text lengths.")]
    [Platform("Win")]
    [Category("UIAutomationProbe")]
    public async Task TextUnitCharacter_WhenTextShapeVaries_ReportsLengthAccuracyAndCost(string platform)
    {
        var executable = platform == "winforms" ? ControlledTestAppPaths.WinForms : ControlledTestAppPaths.Avalonia;
        var (controller, ready) = await TestAppProcessController.StartAsync(executable, "document-editor", 114514, TimeSpan.FromSeconds(30));
        await using (controller)
        {
            using var backend = new WindowsVisualElementBackend();
            using var context = new VisualContext();
            using var acquisition = context.CreateRetention();
            var root = backend.Query(acquisition, VisualElementLocator.FromNativeWindow((nint)ready.Roots.Single().NativeHandle)) ?? throw new InvalidOperationException("Editor root not found.");
            using var snapshot = VisualContextSnapshotter.CreateSnapshot(context, [root.Element], allowedTraverseDirections: VisualContextTraverseDirections.Child);
            var editor = Flatten(snapshot.Roots).Where(node => node.Snapshot.Type == VisualElementType.TextEdit)
                .OrderByDescending(node => node.Snapshot.TextPreview?.Length ?? 0).First().Element;
            var measurements = new List<object>();
            foreach (var (name, sourceText) in CreateTextShapeCases())
            {
                editor.SetText(sourceText);
                var observed = editor.ReadText(0, 65_536);
                Assert.That(observed.Failure, Is.Null);
                Assert.That(observed.NextOffset, Is.Null);
                var observedText = observed.Text ?? string.Empty;
                var probe = ProbeTextUnitCharacter(editor);
                measurements.Add(new
                {
                    name,
                    sourceUtf16Length = sourceText.Length,
                    observedUtf16Length = observedText.Length,
                    observedScalarCount = observedText.EnumerateRunes().Count(),
                    observedTextElementCount = new System.Globalization.StringInfo(observedText).LengthInTextElements,
                    probe.IsSupported,
                    probe.MovedUnits,
                    probe.IsRangeEndAligned,
                    probe.MoveCalls,
                    probe.Elapsed,
                    utf16Delta = probe.MovedUnits - observedText.Length,
                    probe.Failure,
                });
            }

            var output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", $"text-unit-character-{platform}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? throw new InvalidOperationException());
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.Progress.WriteLine(JsonSerializer.Serialize(new { platform, output, measurements }));
        }
    }

    [TestCase("winforms")]
    [TestCase("avalonia")]
    [Explicit("Launches a controlled editor, changes its text, and temporarily suspends its UI thread.")]
    [Platform("Win")]
    public async Task ReadText_WhenEditorIsPagedMutatedAndSuspended_PreservesPagesAndRecovers(string platform)
    {
        var executable = platform == "winforms" ? ControlledTestAppPaths.WinForms : ControlledTestAppPaths.Avalonia;
        var (controller, ready) = await TestAppProcessController.StartAsync(executable, "document-editor", 114514, TimeSpan.FromSeconds(30));
        await using (controller)
        {
            using var backend = new WindowsVisualElementBackend();
            using var context = new VisualContext();
            using var acquisition = context.CreateRetention();
            var root = backend.Query(acquisition, VisualElementLocator.FromNativeWindow((nint)ready.Roots.Single().NativeHandle)) ?? throw new InvalidOperationException("Editor root not found.");
            using var snapshot = VisualContextSnapshotter.CreateSnapshot(context, [root.Element], allowedTraverseDirections: VisualContextTraverseDirections.Child);
            var editor = Flatten(snapshot.Roots).Where(node => node.Snapshot.Type == VisualElementType.TextEdit)
                .OrderByDescending(node => node.Snapshot.TextPreview?.Length ?? 0).First().Element;
            var expected = string.Join(Environment.NewLine, Enumerable.Range(0, 200).Select(index => $"Line {index:D4}: multilingual 中文 العربية 😀 e\u0301 — bounded text paging."));
            editor.SetText(expected);
            var baseline = editor.ReadText(0, 65_536);
            Assert.That(baseline.Failure, Is.Null);
            Assert.That(baseline.NextOffset, Is.Null);
            var baselineText = baseline.Text ?? throw new InvalidOperationException("Editor exposed no text.");
            Assert.That(NormalizeLines(baselineText), Is.EqualTo(NormalizeLines(expected)));
            var pages = new List<object>();
            var combined = new StringBuilder();
            var offset = 0;
            var stopwatch = Stopwatch.StartNew();
            for (var pageIndex = 0; pageIndex < 256; pageIndex++)
            {
                var pageTimer = Stopwatch.StartNew();
                var page = editor.ReadText(offset, 257);
                Assert.That(page.Failure, Is.Null);
                combined.Append(page.Text);
                pages.Add(new { offset, length = page.Text?.Length, page.NextOffset, elapsed = pageTimer.Elapsed });
                if (page.NextOffset is not { } next) break;
                Assert.That(next, Is.GreaterThan(offset));
                offset = next;
            }
            Assert.That(combined.ToString(), Is.EqualTo(baselineText), "Sequential pages must reproduce the stable native text stream.");
            var pagingElapsed = stopwatch.Elapsed;
            editor.SetText("Inserted prefix" + Environment.NewLine + expected);
            var changed = editor.ReadText(0, 65_536);
            Assert.That(changed.Failure, Is.Null);
            var overlap = editor.ReadText(250, 257);
            var expectedOverlap = VisualElementTextReadResult.FromSuccess(changed.Text ?? string.Empty, 250, 257);
            Assert.Multiple(() =>
            {
                Assert.That(overlap.Text, Is.EqualTo(expectedOverlap.Text));
                Assert.That(overlap.NextOffset, Is.EqualTo(expectedOverlap.NextOffset));
                Assert.That(overlap.TotalLength?.Kind, Is.EqualTo(VisualTextLengthKind.Exact));
            });
            VisualElementTextReadResult suspended;
            TimeSpan suspendedElapsed;
            using var controlTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await controller.SuspendUiThreadAsync(controlTimeout.Token);
            try
            {
                stopwatch.Restart();
                suspended = editor.ReadText(250, 257);
                suspendedElapsed = stopwatch.Elapsed;
            }
            finally { await controller.ResumeUiThreadAsync(controlTimeout.Token); }
            var recovered = editor.ReadText(250, 257);
            Assert.That(recovered, Is.EqualTo(overlap));
            // A proxy may answer while the UI thread is suspended; report native behavior instead of demanding a timeout.
            var report = new { platform, characterCount = baselineText.Length, pageCount = pages.Count, pagingElapsed, pages, suspendedElapsed, suspendedFailure = suspended.Failure?.Kind.ToString(), recovered = recovered.Failure is null };
            var output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", $"native-text-{platform}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? throw new InvalidOperationException());
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report));
            TestContext.Progress.WriteLine(JsonSerializer.Serialize(new { platform, characterCount = baselineText.Length, pageCount = pages.Count, pagingElapsed, suspendedElapsed, suspendedFailure = suspended.Failure?.Kind.ToString(), output }));
        }
    }

    private static string NormalizeLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static IReadOnlyDictionary<string, string> CreateTextShapeCases() => new Dictionary<string, string>
    {
        ["empty"] = string.Empty,
        ["ascii"] = new string('a', 4_096),
        ["crlf"] = string.Join("\r\n", Enumerable.Range(0, 512).Select(index => $"Line {index:D4}")),
        ["cjk-arabic"] = string.Concat(Enumerable.Repeat("中文 العربية ", 512)),
        ["surrogate-pairs"] = string.Concat(Enumerable.Repeat("😀", 2_048)),
        ["combining-marks"] = string.Concat(Enumerable.Repeat("e\u0301", 2_048)),
        ["mixed"] = string.Join(Environment.NewLine, Enumerable.Range(0, 200).Select(index => $"Line {index:D4}: multilingual 中文 العربية 😀 e\u0301 — bounded text paging.")),
    };

    private static bool TryInitializeProbeEditor(VisualElement element)
    {
        try
        {
            element.SetText("probe");
            return true;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private static TextUnitCharacterProbe ProbeTextUnitCharacter(VisualElement element)
    {
        if (element is not UIAutomationVisualElement uiAutomationElement)
        {
            return new TextUnitCharacterProbe(false, null, null, 0, TimeSpan.Zero, $"Unexpected element type '{element.GetType().Name}'.");
        }

        var reference = GetAutomationElement(uiAutomationElement);
        if (reference is null)
        {
            return new TextUnitCharacterProbe(false, null, null, 0, TimeSpan.Zero, "The UI Automation element was already released.");
        }

        return ProbeTextUnitCharacter(reference);
    }

    private static TextUnitCharacterProbe ProbeTextUnitCharacter(UIAutomationElementReference reference)
    {
        var elementObject = Marshal.GetObjectForIUnknown(GetPointer(reference));
        try
        {
            var nativeElement = (IUIAutomationElement)elementObject;
            var patternObject = nativeElement.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId);
            if (patternObject is not IUIAutomationTextPattern pattern)
            {
                return new TextUnitCharacterProbe(false, null, null, 0, TimeSpan.Zero, "The element does not expose TextPattern.");
            }

            try
            {
                var documentRange = pattern.DocumentRange;
                var origin = documentRange.Clone();
                try
                {
                    var nativeText = documentRange.GetText(-1) ?? string.Empty;
                    origin.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, origin, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
                    var stopwatch = Stopwatch.StartNew();
                    var (moved, isRangeEndAligned, moveCalls) = CountTextUnits(origin, documentRange, TextUnit.TextUnit_Character);
                    stopwatch.Stop();
                    return new TextUnitCharacterProbe(
                        true,
                        moved,
                        isRangeEndAligned,
                        moveCalls,
                        stopwatch.Elapsed,
                        null,
                        nativeText.Length,
                        nativeText.EnumerateRunes().Count(),
                        new System.Globalization.StringInfo(nativeText).LengthInTextElements);
                }
                finally
                {
                    Marshal.ReleaseComObject(origin);
                    Marshal.ReleaseComObject(documentRange);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(patternObject);
            }
        }
        catch (COMException exception)
        {
            return new TextUnitCharacterProbe(false, null, null, 0, TimeSpan.Zero, $"0x{exception.HResult:X8}: {exception.Message}");
        }
        finally
        {
            Marshal.ReleaseComObject(elementObject);
        }
    }

    private static object ProbeTextRetrievalCost(VisualElement element, int expectedLength, bool shouldMeasureMove)
    {
        if (element is not UIAutomationVisualElement uiAutomationElement) throw new InvalidOperationException($"Unexpected element type '{element.GetType().Name}'.");
        var reference = GetAutomationElement(uiAutomationElement) ?? throw new InvalidOperationException("The UI Automation element was already released.");
        var elementObject = Marshal.GetObjectForIUnknown(GetPointer(reference));
        try
        {
            var nativeElement = (IUIAutomationElement)elementObject;
            var patternObject = nativeElement.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId);
            if (patternObject is not IUIAutomationTextPattern pattern) throw new InvalidOperationException("The element does not expose TextPattern.");
            try
            {
                var documentRange = pattern.DocumentRange;
                try
                {
                    var getTextMeasurements = new List<TextRetrievalMeasurement>();
                    foreach (var maximumLength in new[] { 4_096, 16_384, 65_536, 262_144, 1_048_576, -1 })
                    {
                        getTextMeasurements.Add(MeasureGetText(documentRange, maximumLength, 7));
                    }

                    var moveMeasurement = shouldMeasureMove ? MeasureMove(documentRange, 3) : MoveMeasurement.Skipped;
                    return new
                    {
                        expectedLength,
                        getText = getTextMeasurements,
                        move = moveMeasurement,
                    };
                }
                finally
                {
                    Marshal.ReleaseComObject(documentRange);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(patternObject);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(elementObject);
        }
    }

    private static MoveMeasurement MeasureMove(IUIAutomationTextRange documentRange, int iterations)
    {
        var origin = documentRange.Clone();
        try
        {
            origin.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, origin, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
            try
            {
                _ = CountTextUnits(origin, documentRange, TextUnit.TextUnit_Character);
                var durations = new double[iterations];
                var movedUnits = 0;
                var isRangeEndAligned = false;
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < durations.Length; index++)
                {
                    var stopwatch = Stopwatch.StartNew();
                    (movedUnits, isRangeEndAligned, _) = CountTextUnits(origin, documentRange, TextUnit.TextUnit_Character);
                    durations[index] = stopwatch.Elapsed.TotalMilliseconds;
                }
                var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                Array.Sort(durations);
                return new MoveMeasurement(
                    movedUnits,
                    isRangeEndAligned,
                    iterations,
                    durations[0],
                    durations[durations.Length / 2],
                    durations.Average(),
                    allocatedBytes / iterations,
                    null);
            }
            catch (COMException exception)
            {
                return new MoveMeasurement(null, null, 0, null, null, null, null, $"0x{exception.HResult:X8}: {exception.Message}");
            }
        }
        finally
        {
            Marshal.ReleaseComObject(origin);
        }
    }

    private static TextRetrievalMeasurement MeasureGetText(IUIAutomationTextRange range, int maximumLength, int iterations)
    {
        _ = range.GetText(maximumLength);
        var durations = new double[iterations];
        var returnedLength = 0;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < durations.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            var text = range.GetText(maximumLength) ?? string.Empty;
            durations[index] = stopwatch.Elapsed.TotalMilliseconds;
            returnedLength = text.Length;
        }
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(durations);
        return new TextRetrievalMeasurement(
            maximumLength,
            returnedLength,
            iterations,
            durations[0],
            durations[durations.Length / 2],
            durations.Average(),
            allocatedBytes / iterations);
    }

    private static (int MovedUnits, bool IsRangeEndAligned, int MoveCalls) CountTextUnits(
        IUIAutomationTextRange origin,
        IUIAutomationTextRange target,
        TextUnit unit)
    {
        var startCursor = origin.Clone();
        var endCursor = target.Clone();
        try
        {
            endCursor.MoveEndpointByRange(
                TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                endCursor,
                TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
            var startDistance = startCursor.Move(unit, int.MaxValue);
            var endDistance = endCursor.Move(unit, int.MaxValue);
            var isRangeEndAligned = startCursor.CompareEndpoints(
                TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                endCursor,
                TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start) == 0;
            return (startDistance - endDistance, isRangeEndAligned, 2);
        }
        finally
        {
            Marshal.ReleaseComObject(endCursor);
            Marshal.ReleaseComObject(startCursor);
        }
    }

    private static IReadOnlyList<nint> EnumerateWindowTree(nint root)
    {
        var windows = new List<nint> { root };
        EnumChildWindows(root, (window, _) =>
        {
            windows.Add(window);
            return true;
        }, 0);
        return windows;
    }

    private static string GetWindowClass(nint window)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(window, buffer, buffer.Capacity) == 0 ? string.Empty : buffer.ToString();
    }

    private static IEnumerable<VisualContextSnapshotNode> Flatten(IReadOnlyList<VisualContextSnapshotNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children)) yield return child;
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_automationElement")]
    private static extern ref UIAutomationElementReference? GetAutomationElement(UIAutomationVisualElement element);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_pointer")]
    private static extern ref nint GetPointer(ComReference reference);

    private delegate bool WindowCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, WindowCallback callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maximumCount);

    private sealed record TextUnitCharacterProbe(
        bool IsSupported,
        int? MovedUnits,
        bool? IsRangeEndAligned,
        int MoveCalls,
        TimeSpan Elapsed,
        string? Failure,
        int? NativeTextUtf16Length = null,
        int? NativeTextScalarCount = null,
        int? NativeTextElementCount = null);

    private sealed record TextRetrievalMeasurement(
        int MaximumLength,
        int ReturnedLength,
        int Iterations,
        double MinimumMilliseconds,
        double MedianMilliseconds,
        double MeanMilliseconds,
        long AllocatedBytesPerCall);

    private sealed record MoveMeasurement(
        int? MovedUnits,
        bool? IsRangeEndAligned,
        int Iterations,
        double? MinimumMilliseconds,
        double? MedianMilliseconds,
        double? MeanMilliseconds,
        long? AllocatedBytesPerCall,
        string? Failure)
    {
        public static MoveMeasurement Skipped { get; } = new(null, null, 0, null, null, null, null, "Skipped above the configured probe threshold.");
    }
}
