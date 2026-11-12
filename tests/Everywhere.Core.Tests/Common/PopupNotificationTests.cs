using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Views;
using ShadUI;

namespace Everywhere.Core.Tests.Common;

public sealed class PopupNotificationTests
{
    // The notice must survive closing its first window without creating a second simultaneous
    // presentation or losing local View state (the upgrade dialog's trash choice).
    [Test]
    public Task HideFirstWindow_MovesSameViewToSecond_OnlyExplicitConfirmationPersists() => RunAsync(async () =>
    {
        var storage = new InMemoryKeyValueStorage();
        using var center = new NotificationCenter(storage);
        var content = new CheckBox { IsChecked = true };
        var notification = CreateNotification("upgrade") with { Content = content };
        var response = center.ShowPopupAsync("local", notification, CancellationToken.None);
        var firstHost = new DialogHost();
        var secondHost = new DialogHost();
        var first = new Window { Content = firstHost };
        var second = new Window { Content = secondHost };
        try
        {
            first.Show();
            first.Activate();
            using var firstRegistration = center.RegisterPopupWindow(first, NotificationWindows.Main);
            await DrainAsync();
            var view = firstHost.Dialog;
            Assert.That(view, Is.TypeOf<PopupNotificationView>());

            // A feed refresh must not replace what is being read, but the next owner must receive
            // the latest descriptor even when a subsequent HTTP refresh would return 304.
            var updated = notification with { TitleKey = new DirectLocaleKey("Updated title") };
            center.ReplacePopups("local", [updated]);
            Assert.That(view.DataContext, Is.SameAs(notification));

            second.Show();
            second.Activate();
            using var secondRegistration = center.RegisterPopupWindow(second, NotificationWindows.Chat);
            await DrainAsync();
            Assert.That(secondHost.Dialog, Is.Null);

            first.Hide();
            await DrainAsync();
            Assert.Multiple(() =>
            {
                Assert.That(secondHost.Dialog, Is.SameAs(view));
                Assert.That(view.DataContext, Is.SameAs(updated));
                Assert.That(response.IsCompleted, Is.False);
                Assert.That(content.IsChecked, Is.True);
            });

            Acknowledge(secondHost);
            // Unlike a cross-window transfer, normal closure must retain content for the host's
            // exit animation rather than clearing Dialog synchronously.
            Assert.That(secondHost.IsDialogOpen, Is.False);
            Assert.That(secondHost.Dialog, Is.SameAs(view));
            Assert.That(await response, Is.True);
            Assert.That(await center.ShowPopupAsync("local", notification, CancellationToken.None), Is.False);
        }
        finally
        {
            first.Close();
            second.Close();
        }

        using var nextLaunch = new NotificationCenter(storage);
        Assert.That(await nextLaunch.ShowPopupAsync("local", notification, CancellationToken.None), Is.False);
    });

    [Test]
    public Task BackgroundCancellation_ConfirmationWinsOnUiThread_ReturnsAcknowledged() => RunAsync(async () =>
    {
        using var center = new NotificationCenter(new InMemoryKeyValueStorage());
        using var cancellation = new CancellationTokenSource();
        var notification = CreateNotification("confirmation-race");
        var response = center.ShowPopupAsync("local", notification, cancellation.Token);
        var host = new DialogHost();
        var window = new Window { Content = host };
        try
        {
            window.Show();
            window.Activate();
            using var registration = center.RegisterPopupWindow(window, NotificationWindows.Main);
            await DrainAsync();

            // Hold the UI thread until cancellation is queued, then handle a click before pumping
            // that queue. The center must not persist acknowledgment but return cancellation.
            var cancellationThread = new Thread(cancellation.Cancel);
            cancellationThread.Start();
            cancellationThread.Join();
            Acknowledge(host);
            Assert.That(await response, Is.True);
            Assert.That(await center.ShowPopupAsync("local", notification, CancellationToken.None), Is.False);
        }
        finally
        {
            window.Close();
        }
    });

    [Test]
    public Task CancelPendingNotice_DoesNotAcknowledge_AndNextRegistrationCanShow() => RunAsync(async () =>
    {
        using var center = new NotificationCenter(new InMemoryKeyValueStorage());
        using var cancellation = new CancellationTokenSource();
        var notification = CreateNotification("pending");
        var response = center.ShowPopupAsync("local", notification, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThatAsync(async () => await response, Throws.InstanceOf<OperationCanceledException>());

        var host = new DialogHost();
        var window = new Window { Content = host };
        try
        {
            window.Show();
            window.Activate();
            using var registration = center.RegisterPopupWindow(window, NotificationWindows.Main);
            center.ReplacePopups("local", [notification]);
            await DrainAsync();
            Assert.That(host.Dialog, Is.TypeOf<PopupNotificationView>());
            center.ReplacePopups("local", []);
            Assert.That(host.HasOpenedDialog, Is.False);
        }
        finally
        {
            window.Close();
        }
    });

    private static PopupNotification CreateNotification(string id) => new()
    {
        Id = id,
        TitleKey = new DirectLocaleKey("Title"),
        AcknowledgeKey = new DirectLocaleKey("OK"),
        Content = new TextBlock { Text = "Content" }
    };

    private static void Acknowledge(DialogHost host)
    {
        if (host.Dialog is not PopupNotificationView view || view.FindControl<Button>("AcknowledgeButton") is not { } button)
        {
            Assert.Fail("The notification acknowledgment button is missing.");
            return;
        }

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task DrainAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static Task<bool> RunAsync(Func<Task> action)
    {
        return HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(PopupNotificationTests).Assembly)
            .Dispatch(async () =>
            {
                await action();
                return true;
            }, CancellationToken.None);
    }
}