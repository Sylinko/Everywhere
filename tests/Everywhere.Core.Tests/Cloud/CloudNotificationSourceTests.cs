using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Everywhere.Cloud;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Views;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShadUI;

namespace Everywhere.Core.Tests.Cloud;

[NonParallelizable]
public sealed class CloudNotificationSourceTests
{
    [AvaloniaTest]
    public async Task AccountChanges_PreserveDeviceNoticeAndIsolateAcknowledgment()
    {
        // Signing in must not interrupt a public announcement; confirming an account announcement
        // for B must not suppress the same announcement when A signs in on the same device.
        using var server = new NotificationServer(() => Task.FromResult(Feed(("public", "device"), ("member", "account"))));
        using var center = new NotificationCenter(new InMemoryKeyValueStorage());
        var cloud = Substitute.For<ICloudClient>();
        SetAccount(cloud, "a");
        using var source = new CloudNotificationSource(server, center, cloud, NullLogger<CloudNotificationSource>.Instance);
        var host = new DialogHost();
        var window = new Window { Content = host };
        try
        {
            window.Show();
            window.Activate();
            using var registration = center.RegisterPopupWindow(window, NotificationWindows.Main);
            await source.InitializeAsync(CancellationToken.None);
            await DrainAsync();
            var publicView = host.Dialog;
            Assert.That(CurrentId(host), Is.EqualTo("public"));

            SetAccount(cloud, "b");
            await DrainAsync();
            Assert.That(host.Dialog, Is.SameAs(publicView));
            Assert.That(server.Requests, Has.Count.EqualTo(2));
            Assert.That(server.Requests.All(request => request.ClientName == nameof(CloudNotificationSource)), Is.True);
            Assert.That(server.Requests[1].EntityTag, Is.Empty);

            Acknowledge(host);
            await DrainAsync();
            Assert.That(CurrentId(host), Is.EqualTo("member"));
            Acknowledge(host);
            await DrainAsync();
            Assert.That(host.HasOpenedDialog, Is.False);

            SetAccount(cloud, "a");
            await DrainAsync();
            Assert.That(host.HasOpenedDialog, Is.True);
            Assert.That(CurrentId(host), Is.EqualTo("member"));
            SetAccount(cloud, null);
            Assert.That(host.HasOpenedDialog, Is.False);
            Assert.That(server.Requests[^1].ClientName, Is.EqualTo(CloudNotificationSource.AnonymousHttpClientName));

            // Withdrawal on sign-out is not acknowledgment, so A sees it again after signing in.
            SetAccount(cloud, "a");
            await DrainAsync();
            Assert.That(host.HasOpenedDialog, Is.True);
            Assert.That(CurrentId(host), Is.EqualTo("member"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task LanguageChanges_WaitForNormalRefresh_OnlyReuseSameLanguageEntityTag()
    {
        // Language is a request parameter, not a trigger: changing it must neither cancel an
        // in-flight read nor bypass the window-refresh throttle. Account changes still cancel.
        var previousLocale = LocaleManager.CurrentLocale;
        try
        {
            LocaleManager.CurrentLocale = LocaleName.En;
            var oldReply = new TaskCompletionSource<HttpResponseMessage>();
            using var server = new NotificationServer(() => oldReply.Task);
            using var center = new NotificationCenter(new InMemoryKeyValueStorage());
            var cloud = Substitute.For<ICloudClient>();
            cloud.LoginStatus.Returns(CloudClientLoginStatus.AutoLoggingIn);
            using var source = new CloudNotificationSource(server, center, cloud, NullLogger<CloudNotificationSource>.Instance);
            var host = new DialogHost();
            var window = new Window { Content = host };
            try
            {
                window.Show();
                window.Activate();
                using var registration = center.RegisterPopupWindow(window, NotificationWindows.Main);
                await source.InitializeAsync(CancellationToken.None);
                Assert.That(server.Requests, Is.Empty);
                SetAccount(cloud, "a");
                Assert.That(server.Requests, Has.Count.EqualTo(1));
                Assert.That(server.Requests[0].Language, Is.EqualTo("en"));

                server.Reply = () => Task.FromResult(Feed(("current", "account")));
                LocaleManager.CurrentLocale = LocaleName.ZhHans;
                await DrainAsync();
                Assert.That(server.Requests, Has.Count.EqualTo(1));
                Assert.That(server.Requests[0].Token.IsCancellationRequested, Is.False);

                SetAccount(cloud, "b");
                await DrainAsync();
                Assert.That(server.Requests[0].Token.IsCancellationRequested, Is.True);
                Assert.That(server.Requests[1].Language, Is.EqualTo("zh-Hans"));
                Assert.That(server.Requests[1].EntityTag, Is.Empty);
                var currentView = host.Dialog;
                Assert.That(CurrentId(host), Is.EqualTo("current"));

                oldReply.SetResult(Feed(("obsolete", "account")));
                await DrainAsync();
                Assert.That(host.Dialog, Is.SameAs(currentView));
                Acknowledge(host);
                await DrainAsync();
                Assert.That(host.HasOpenedDialog, Is.False);

                server.Reply = () => Task.FromResult(Feed());
                LocaleManager.CurrentLocale = LocaleName.Fr;
                await DrainAsync();
                window.Hide();
                window.Show();
                window.Activate();
                await DrainAsync();
                Assert.That(server.Requests, Has.Count.EqualTo(2));

                ExpireRefreshInterval(source);
                window.Hide();
                window.Show();
                window.Activate();
                await DrainAsync();
                Assert.That(server.Requests, Has.Count.EqualTo(3));
                Assert.That(server.Requests[^1].Language, Is.EqualTo("fr"));
                Assert.That(server.Requests[^1].EntityTag, Is.Empty);

                server.Reply = () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
                ExpireRefreshInterval(source);
                window.Hide();
                window.Show();
                window.Activate();
                await DrainAsync();
                Assert.That(server.Requests, Has.Count.EqualTo(4));
                Assert.That(server.Requests[^1].EntityTag, Is.EqualTo("\"feed\""));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            LocaleManager.CurrentLocale = previousLocale;
        }
    }

    private static void SetAccount(ICloudClient cloud, string? accountId)
    {
        cloud.UserProfile.Returns(accountId is null ? null : new UserProfile
        {
            Subject = accountId,
            Name = "Test",
            Email = "test@example.invalid"
        });
        cloud.LoginStatus.Returns(accountId is null ? CloudClientLoginStatus.NotLoggedIn : CloudClientLoginStatus.LoggedIn);
        cloud.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(cloud, new PropertyChangedEventArgs(nameof(ICloudClient.LoginStatus)));
    }

    private static string? CurrentId(DialogHost host) => (host.Dialog?.DataContext as PopupNotification)?.Id;

    private static void ExpireRefreshInterval(CloudNotificationSource source)
    {
        // Simulate the ordinary five-minute interval without adding a production clock/test hook.
        var field = typeof(CloudNotificationSource).GetField("_lastAttempt", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertionException("Missing refresh timestamp.");
        field.SetValue(source, DateTimeOffset.MinValue);
    }

    private static void Acknowledge(DialogHost host)
    {
        var button = (host.Dialog as PopupNotificationView)?.FindControl<Button>("AcknowledgeButton")
            ?? throw new AssertionException("No notification acknowledgment button.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task DrainAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static HttpResponseMessage Feed(params (string Id, string Scope)[] items)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers = { ETag = new EntityTagHeaderValue("\"feed\"") },
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                items = items.Select(item => new
                {
                    id = item.Id,
                    scope = item.Scope,
                    title = new { en = item.Id },
                    content = new { format = "markdown", text = new { en = "Content" } },
                    windows = new[] { "main", "chat" },
                    repeat = "once"
                })
            }))
        };
    }

    private sealed class NotificationServer(Func<Task<HttpResponseMessage>> reply) : HttpMessageHandler, IHttpClientFactory
    {
        public Func<Task<HttpResponseMessage>> Reply { get; set; } = reply;
        public List<Request> Requests { get; } = [];

        private string _clientName = string.Empty;

        public HttpClient CreateClient(string name)
        {
            _clientName = name;
            return new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://notifications.example.invalid/") };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.That(request.RequestUri?.AbsolutePath, Is.EqualTo("/notifications/v1"));
            Requests.Add(new Request(
                _clientName,
                request.Headers.AcceptLanguage.ToString(),
                request.Headers.IfNoneMatch.ToString(),
                cancellationToken));
            return Reply();
        }
    }

    private sealed record Request(string ClientName, string Language, string EntityTag, CancellationToken Token);
}