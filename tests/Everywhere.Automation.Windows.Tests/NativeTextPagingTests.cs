using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Everywhere.Automation.TestApp;
using Everywhere.Windows.Automation;

namespace Everywhere.Automation.Windows.Tests;

/// <summary>Exercises the production text reader against controlled native multiline editors.</summary>
public sealed class NativeTextPagingTests
{
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
            Assert.That(overlap, Is.EqualTo(VisualElementTextReadResult.FromSuccess(changed.Text ?? string.Empty, 250, 257)));
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

    private static IEnumerable<VisualContextSnapshotNode> Flatten(IReadOnlyList<VisualContextSnapshotNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children)) yield return child;
        }
    }
}
