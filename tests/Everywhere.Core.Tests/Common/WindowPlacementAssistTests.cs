using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;

namespace Everywhere.Core.Tests.Common;

public sealed class WindowPlacementAssistTests
{
    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public void Attach_WhenWindowMovesAndResizes_ReusesStorageKeyAndRestoresLatestBounds(bool shouldShowBeforeAttach)
    {
        var storage = new InMemoryKeyValueStorage();
        var window = new Window { Width = 300, Height = 200 };
        var restoredWindow = new Window();

        try
        {
            if (shouldShowBeforeAttach)
            {
                window.Show();
            }

            WindowPlacementAssist.Attach(window, storage, "TestWindow");

            if (!shouldShowBeforeAttach)
            {
                window.Show();
            }

            Dispatcher.UIThread.RunJobs();

            window.Position = new PixelPoint(100, 120);
            window.Width = 500;
            window.Height = 350;
            window.UpdateLayout();
            // Headless platform resize notifications are queued separately from layout.
            Dispatcher.UIThread.RunJobs();

            window.Position = new PixelPoint(200, 220);
            window.Width = 600;
            window.Height = 400;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var expected = new WindowPlacement(window.Position, 600, 400, WindowState.Normal);

            var saved = storage.Get<WindowPlacement?>("WindowPlacement.TestWindow");
            Assert.That((saved?.Position, saved?.Width, saved?.Height, saved?.WindowState),
                Is.EqualTo((expected.Position, expected.Width, expected.Height, expected.WindowState)));
            Assert.That(storage.Contains("WindowPlacement.WindowPlacement.TestWindow"), Is.False);

            window.Close();
            WindowPlacementAssist.Attach(restoredWindow, storage, "TestWindow");
            restoredWindow.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.That(restoredWindow.Position, Is.EqualTo(expected.Position));
            Assert.That(restoredWindow.Width, Is.EqualTo(expected.Width));
            Assert.That(restoredWindow.Height, Is.EqualTo(expected.Height));
        }
        finally
        {
            window.Close();
            restoredWindow.Close();
        }
    }
}
