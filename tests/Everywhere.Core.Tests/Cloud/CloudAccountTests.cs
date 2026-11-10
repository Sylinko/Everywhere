using System.Net;
using System.Reflection;
using System.Threading.Channels;
using Avalonia.Headless.NUnit;
using Everywhere.Cloud;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.Cloud;

[NonParallelizable]
public sealed class CloudAccountTests
{
    [AvaloniaTest]
    public async Task Login_WhenSubscriptionIsSlowOrFails_PublishesIdentityAndReusesTheInitialRequest()
    {
        // Regression: the avatar appeared while login/flyout readiness still waited for billing.
        // A flyout joining that read may cancel its wait without canceling the login-owned request.
        using var network = new AccountServer();
        var cloud = new OAuthCloudClient(network, NullLogger<OAuthCloudClient>.Instance);
        SetToken(cloud, "a");
        await cloud.LoginAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(cloud.LoginStatus, Is.EqualTo(CloudClientLoginStatus.LoggedIn), cloud.LastLoginErrorKey?.ToString());
        Assert.That(cloud.UserProfile?.Subject, Is.EqualTo("a"));
        Assert.That(cloud.IsSubscriptionLoading, Is.True);
        var initial = SubscriptionTask(cloud);
        var initialRequest = await network.NextSubscriptionAsync();
        using var wait = new CancellationTokenSource();
        var joined = ((ICloudClient)cloud).ReloadUserDataAsync(wait.Token);
        wait.Cancel();
        await joined;
        Assert.That(network.ProfilesRead, Is.EqualTo(1));
        Assert.That(initialRequest.Token.IsCancellationRequested, Is.False);
        initialRequest.Reply.SetResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await initial.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(cloud.IsSubscriptionLoading, Is.False);
        Assert.That(cloud.SubscriptionErrorKey, Is.Not.Null);
        Assert.That(cloud.LastLoginErrorKey, Is.Null);
        Assert.That(cloud.LoginStatus, Is.EqualTo(CloudClientLoginStatus.LoggedIn));

        var retry = ((ICloudClient)cloud).ReloadUserDataAsync(CancellationToken.None);
        var retriedRequest = await network.NextSubscriptionAsync();
        retriedRequest.Reply.SetResult(Success("Plus"));
        await retry;
        Assert.That(cloud.Subscription?.Plan, Is.EqualTo(SubscriptionPlan.Plus));
        Assert.That(cloud.SubscriptionErrorKey, Is.Null);
        Assert.That(cloud.IsSubscriptionLoading, Is.False);
    }

    [AvaloniaTest]
    public async Task Subscription_WhenAccountChanges_DoesNotPublishAnOldAccount()
    {
        // Account isolation must also hold for a response already arriving when cancellation wins.
        using var network = new AccountServer();
        var cloud = new OAuthCloudClient(network, NullLogger<OAuthCloudClient>.Instance);
        SetToken(cloud, "a");
        await cloud.LoginAsync(CancellationToken.None);
        var old = SubscriptionTask(cloud);
        var oldRequest = await network.NextSubscriptionAsync();
        SetToken(cloud, "b");
        await cloud.LoginAsync(CancellationToken.None);
        var current = SubscriptionTask(cloud);
        var currentRequest = await network.NextSubscriptionAsync();
        Assert.That(oldRequest.Token.IsCancellationRequested, Is.True);
        currentRequest.Reply.SetResult(Success("Pro"));
        await current;
        oldRequest.Reply.SetResult(Success("Starter")); // The test server deliberately ignores cancellation.
        await old;
        Assert.That(cloud.UserProfile?.Subject, Is.EqualTo("b"));
        Assert.That(cloud.Subscription?.Plan, Is.EqualTo(SubscriptionPlan.Pro));
        Assert.That(cloud.SubscriptionErrorKey, Is.Null);
    }

    // Use a fresh in-memory token only: Initialize/SetToken/Logout would access the real OS vault.
    private static void SetToken(OAuthCloudClient cloud, string account)
    {
        var session = Field(cloud, "_session");
        var field = session.GetType().GetField("_tokenData", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertionException("Missing token state.");
        var constructor = field.FieldType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(item => item.GetParameters().Length == 4);
        field.SetValue(session, constructor.Invoke(["synthetic-refresh", account, "synthetic-id", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()]));
    }

    private static Task SubscriptionTask(OAuthCloudClient cloud) => (Task)Field(cloud, "_subscriptionTask");

    private static object Field(object owner, string name)
    {
        return owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new AssertionException($"Missing field {name}.");
    }

    private static HttpResponseMessage Success(string plan)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"success\":true,\"data\":{\"plan\":\"" + plan + "\",\"bonusCredits\":123}}",
                System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class AccountServer : HttpMessageHandler, IHttpClientFactory
    {
        public int ProfilesRead { get; private set; }
        private readonly Channel<(TaskCompletionSource<HttpResponseMessage> Reply, CancellationToken Token)> _requests =
            Channel.CreateUnbounded<(TaskCompletionSource<HttpResponseMessage>, CancellationToken)>();

        public Task<(TaskCompletionSource<HttpResponseMessage> Reply, CancellationToken Token)> NextSubscriptionAsync()
        {
            return _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://account.example.invalid") };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (path == "/api/auth/oauth2/userinfo")
            {
                ProfilesRead++;
                var account = request.Headers.Authorization?.Parameter;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"sub\":\"{account}\",\"name\":\"Test\",\"email\":\"test@example.invalid\"}}",
                        System.Text.Encoding.UTF8, "application/json")
                });
            }
            Assert.That(path, Is.EqualTo("/api/subscription"), "No vault, OAuth token or external service requests are allowed.");
            var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Writer.TryWrite((reply, cancellationToken));
            return reply.Task;
        }
    }
}