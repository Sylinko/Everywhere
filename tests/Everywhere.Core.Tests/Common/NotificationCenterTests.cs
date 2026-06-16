using Avalonia.Controls.Notifications;
using Avalonia.Headless;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.I18N;

namespace Everywhere.Core.Tests.Common;

public sealed class NotificationCenterTests
{
    [Test]
    public async Task Push_ExposesCategory()
    {
        await RunOnUiThreadAsync(() =>
        {
            using var center = new NotificationCenter(new InMemoryKeyValueStorage());
            var publisher = center.GetPublisher("category.alpha");

            publisher.Push("hello", new DirectLocaleKey("Hello"));

            Assert.Multiple(() =>
            {
                Assert.That(center.Notifications, Has.Count.EqualTo(1));
                Assert.That(center.Notifications[0].Id, Is.EqualTo("hello"));
                Assert.That(center.Notifications[0].Category, Is.EqualTo("category.alpha"));
            });
        });
    }

    [Test]
    public async Task Push_AllowsSameIdInDifferentCategories()
    {
        await RunOnUiThreadAsync(() =>
        {
            using var center = new NotificationCenter(new InMemoryKeyValueStorage());
            var alpha = center.GetPublisher("category.alpha");
            var beta = center.GetPublisher("category.beta");

            alpha.Push("shared", new DirectLocaleKey("Alpha"));
            beta.Push("shared", new DirectLocaleKey("Beta"), NotificationType.Error);

            Assert.Multiple(() =>
            {
                Assert.That(center.Notifications, Has.Count.EqualTo(2));
                Assert.That(center.Notifications.Select(n => n.Category), Is.EquivalentTo(new[] { "category.alpha", "category.beta" }));
                Assert.That(center.Notifications.Select(n => n.Id), Is.EqualTo(new[] { "shared", "shared" }));
            });
        });
    }

    [Test]
    public async Task CategoryOperations_AffectOnlyThatCategory()
    {
        await RunOnUiThreadAsync(() =>
        {
            using var center = new NotificationCenter(new InMemoryKeyValueStorage());
            var alpha = center.GetPublisher("category.alpha");
            var beta = center.GetPublisher("category.beta");

            alpha.Push("a1", new DirectLocaleKey("A1"));
            alpha.Push("a2", new DirectLocaleKey("A2"));
            beta.Push("b1", new DirectLocaleKey("B1"));

            alpha.Dismiss("a1");
            Assert.That(center.Notifications.Select(n => (n.Category, n.Id)), Is.EquivalentTo(new[]
            {
                ("category.alpha", "a2"),
                ("category.beta", "b1")
            }));

            alpha.Reset(
                new[]
                {
                    new DynamicNotificationDescriptor("a3", new DirectLocaleKey("A3")),
                    new DynamicNotificationDescriptor("a4", new DirectLocaleKey("A4"))
                });
            Assert.That(center.Notifications.Select(n => (n.Category, n.Id)), Is.EquivalentTo(new[]
            {
                ("category.alpha", "a3"),
                ("category.alpha", "a4"),
                ("category.beta", "b1")
            }));

            alpha.Clear();
            Assert.That(center.Notifications.Select(n => (n.Category, n.Id)), Is.EqualTo(new[]
            {
                ("category.beta", "b1")
            }));
        });
    }

    [Test]
    public async Task DismissCommand_PersistsDismissalUntilForceShow()
    {
        await RunOnUiThreadAsync(() =>
        {
            using var center = new NotificationCenter(new InMemoryKeyValueStorage());
            var publisher = center.GetPublisher("category.alpha");

            publisher.Push("dismissible", new DirectLocaleKey("Dismissible"));
            var notification = center.Notifications.Single();

            notification.DismissCommand!.Execute(notification);
            publisher.Push("dismissible", new DirectLocaleKey("Dismissible"));

            Assert.That(center.Notifications, Is.Empty);

            publisher.Push("dismissible", new DirectLocaleKey("Dismissible"), forceShow: true);

            Assert.Multiple(() =>
            {
                Assert.That(center.Notifications, Has.Count.EqualTo(1));
                Assert.That(center.Notifications[0].Id, Is.EqualTo("dismissible"));
            });
        });
    }

    [Test]
    public async Task Notifications_AreSortedByTypeThenNewestFirst()
    {
        await RunOnUiThreadAsync(() =>
        {
            using var center = new NotificationCenter(new InMemoryKeyValueStorage());
            var publisher = center.GetPublisher("category.alpha");

            publisher.Push("info", new DirectLocaleKey("Info"));
            publisher.Push("warning-old", new DirectLocaleKey("Warning"), NotificationType.Warning);
            publisher.Push("error", new DirectLocaleKey("Error"), NotificationType.Error);
            publisher.Push("success", new DirectLocaleKey("Success"), NotificationType.Success);
            publisher.Push("warning-new", new DirectLocaleKey("Warning"), NotificationType.Warning);

            Assert.That(center.Notifications.Select(n => n.Id), Is.EqualTo(new[]
            {
                "error",
                "warning-new",
                "warning-old",
                "success",
                "info"
            }));
        });
    }

    [Test]
    public async Task Reset_PreservesInputOrderWithinSameType()
    {
        await RunOnUiThreadAsync(() =>
        {
            using var center = new NotificationCenter(new InMemoryKeyValueStorage());
            var publisher = center.GetPublisher("category.alpha");

            publisher.Reset(
                new[]
                {
                    new DynamicNotificationDescriptor("first", new DirectLocaleKey("First")),
                    new DynamicNotificationDescriptor("second", new DirectLocaleKey("Second")),
                    new DynamicNotificationDescriptor("third", new DirectLocaleKey("Third"))
                });
            publisher.Push("new", new DirectLocaleKey("New"));

            Assert.That(center.Notifications.Select(n => n.Id), Is.EqualTo(new[]
            {
                "new",
                "first",
                "second",
                "third"
            }));
        });
    }

    private static Task RunOnUiThreadAsync(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(NotificationCenterTests).Assembly)
            .Dispatch(action, CancellationToken.None);
}
