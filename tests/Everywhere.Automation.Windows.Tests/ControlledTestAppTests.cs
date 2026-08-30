using Avalonia;
using Everywhere.Automation.TestApp;
using Everywhere.Windows.Automation;

namespace Everywhere.Automation.Windows.Tests;

public sealed class ControlledTestAppTests
{
    [TestCase("winforms")]
    [TestCase("avalonia")]
    [TestCase("cefsharp")]
    [Explicit("Launches visible controlled target processes and requires an interactive Windows desktop.")]
    [Platform("Win")]
    [Category("ControlledTestApp")]
    public async Task MoveNext_WhenControlledTargetIsReady_AdvancesOneRevision(string backend)
    {
        var executablePath = GetExecutablePath(backend);
        Assert.That(File.Exists(executablePath), Is.True, $"Build the '{backend}' TestApp before running this tier.");
        var (controller, ready) = await TestAppProcessController.StartAsync(executablePath, "chat", 42, TimeSpan.FromSeconds(30));
        await using (controller)
        {
            await controller.SendAsync(new TestAppCommand(TestAppCommandKind.MoveNext));
            var advanced = await controller.ReadStatusAsync();
            Assert.Multiple(() =>
            {
                Assert.That(ready.Kind, Is.EqualTo(TestAppStatusKind.Ready));
                Assert.That(ready.ProcessId, Is.EqualTo(controller.Process.Id));
                Assert.That(ready.Roots, Has.Count.EqualTo(1));
                Assert.That(ready.Roots[0].NativeHandle, Is.Not.Zero);
                Assert.That(advanced.Kind, Is.EqualTo(TestAppStatusKind.Advanced));
                Assert.That(advanced.Step, Is.EqualTo(1));
                Assert.That(advanced.Revision, Is.EqualTo(1));
            });
        }
    }

    [Test]
    [Explicit("Launches a visible WinForms target and exercises the production Windows UIA reader.")]
    [Platform("Win")]
    [Category("ControlledTestApp")]
    public async Task Query_WhenWinFormsTargetIsReady_UsesSharedProductionBackend()
    {
        var executablePath = GetExecutablePath("winforms");
        Assert.That(File.Exists(executablePath), Is.True, "Build the WinForms TestApp before running this tier.");
        var (controller, ready) = await TestAppProcessController.StartAsync(executablePath, "chat", 42, TimeSpan.FromSeconds(30));
        await using (controller)
        {
            using var visualElementBackend = new WindowsVisualElementBackend();
            using var visualContext = new VisualContext();
            using var retention = visualContext.CreateRetention();
            var rootHandle = (nint)ready.Roots.Single().NativeHandle;
            var request = new VisualElementQueryRequest(VisualElementFields.Id | VisualElementFields.Type | VisualElementFields.Name | VisualElementFields.Bounds | VisualElementFields.ProcessId | VisualElementFields.NativeWindowHandle, 256);
            var root = visualElementBackend.Query(retention, VisualElementLocator.FromNativeWindow(rootHandle), request: request) ?? throw new InvalidOperationException("The Windows reader did not resolve the target HWND.");

            VisualElementQueryResult child;
            using (var children = root.Element.CreateEnumerator(VisualElementRelation.Child, new VisualElementEnumerationOptions(request)))
            {
                Assert.That(children.HasMore, Is.True);
                Assert.That(children.MoveNext(), Is.True);
                retention.Retain(children.Current.Element);
                child = children.Current;
            }

            Assert.Multiple(() =>
            {
                Assert.That(root.IsSuccess, Is.True);
                Assert.That(root.Snapshot.Id, Does.StartWith("uia:"));
                Assert.That(root.Snapshot.Type, Is.EqualTo(VisualElementType.TopLevel));
                Assert.That(root.Snapshot.ProcessId, Is.EqualTo(controller.Process.Id));
                Assert.That(root.Snapshot.NativeWindowHandle, Is.EqualTo(rootHandle));
                Assert.That(child.IsSuccess, Is.True);
            });

            var repeatedRoot = visualElementBackend.Query(retention, VisualElementLocator.FromNativeWindow(rootHandle), request: request) ?? throw new InvalidOperationException("The Windows reader did not resolve the target HWND a second time.");
            var resolvedTopLevel = visualElementBackend.Query(retention, VisualElementLocator.FromNativeWindow(rootHandle), VisualElementResolution.TopLevel, request) ?? throw new InvalidOperationException("The Windows reader did not resolve the target top-level window.");
            Assert.That(repeatedRoot.Element, Is.SameAs(root.Element));
            Assert.That(root.Element.Query(request).Element, Is.SameAs(root.Element));
            Assert.That(resolvedTopLevel.Element, Is.SameAs(root.Element));

            VisualElementQueryResult screen;
            using (var parents = root.Element.CreateEnumerator(VisualElementRelation.Parent, new VisualElementEnumerationOptions(request)))
            {
                Assert.That(parents.MoveNext(), Is.True);
                retention.Retain(parents.Current.Element);
                screen = parents.Current;
            }

            var screenBounds = screen.Snapshot.Bounds ?? throw new InvalidOperationException("The composed Screen did not expose bounds.");
            var screenAtPoint = visualElementBackend.Query(retention, VisualElementLocator.FromPoint(new PixelPoint(screenBounds.X + screenBounds.Width / 2, screenBounds.Y + screenBounds.Height / 2)), VisualElementResolution.Screen, request) ?? throw new InvalidOperationException("The Windows reader did not resolve the Screen at the requested point.");
            var screenFromWindow = visualElementBackend.Query(retention, VisualElementLocator.FromNativeWindow(rootHandle), VisualElementResolution.Screen, request) ?? throw new InvalidOperationException("The Windows reader did not resolve the Screen containing the target window.");
            Assert.Multiple(() =>
            {
                Assert.That(screen.Element, Is.TypeOf<ScreenVisualElement>());
                Assert.That(screenAtPoint.Element, Is.SameAs(screen.Element));
                Assert.That(screenFromWindow.Element, Is.SameAs(screen.Element));
                Assert.That(screen.Snapshot.Type, Is.EqualTo(VisualElementType.Screen));
            });

            var foundOriginalWindow = false;
            using (var windows = screen.Element.CreateEnumerator(VisualElementRelation.Child, new VisualElementEnumerationOptions(request)))
            {
                for (var index = 0; index < 256 && windows.MoveNext(); index++)
                {
                    if (windows.Current.Snapshot.NativeWindowHandle == rootHandle)
                    {
                        foundOriginalWindow = true;
                        break;
                    }
                }
            }

            Assert.That(foundOriginalWindow, Is.True, "The composed Screen children did not contain the originating top-level window.");
            using var capture = await screen.Element.CaptureAsync();
            Assert.Multiple(() =>
            {
                Assert.That(capture.Data, Is.Not.Zero);
                Assert.That(capture.Size.Width, Is.GreaterThan(0));
                Assert.That(capture.Size.Height, Is.GreaterThan(0));
            });
        }
    }

    private static string GetExecutablePath(string backend) => backend switch
    {
        "winforms" => ControlledTestAppPaths.WinForms,
        "avalonia" => ControlledTestAppPaths.Avalonia,
        "cefsharp" => ControlledTestAppPaths.CefSharp,
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null),
    };
}
