using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Avalonia.Headless.NUnit;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Everywhere.Cloud;
using Everywhere.Core.Tests.Configuration;
using Everywhere.Interop;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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

    [AvaloniaTest]
    public async Task Login_WhenUserInfoReturnsInvalidToken_RefreshesOnceAndCompletesLogin()
    {
        using var secrets = new SecretStoreScope();
        using var network = new AccountServer
        {
            OnUserInfo = accessToken => accessToken == "stale-access"
                ? InvalidTokenResponse()
                : UserInfoSuccess(accessToken),
            OnToken = _ => TokenSuccess("fresh-refresh", "fresh-access", "fresh-id")
        };
        var cloud = new OAuthCloudClient(network, NullLogger<OAuthCloudClient>.Instance);
        SetToken(cloud, "stale-access");

        await cloud.LoginAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(cloud.LoginStatus, Is.EqualTo(CloudClientLoginStatus.LoggedIn), cloud.LastLoginErrorKey?.ToString());
            Assert.That(cloud.UserProfile?.Subject, Is.EqualTo("fresh-access"));
            Assert.That(cloud.LastLoginErrorKey, Is.Null);
            Assert.That(network.ProfilesRead, Is.EqualTo(2));
            Assert.That(network.TokenRequests, Is.EqualTo(1));
        });
    }

    [AvaloniaTest]
    public async Task Login_WhenUserInfoReturnsInvalidTokenAndRefreshRejected_ClearsSessionAndStartsInteractiveLogin()
    {
        using var secrets = new SecretStoreScope();
        using var network = new AccountServer
        {
            OnUserInfo = _ => InvalidTokenResponse(),
            OnToken = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"error":"invalid_grant","error_description":"Refresh token revoked"}""",
                    Encoding.UTF8,
                    "application/json")
            }
        };
        var cloud = new OAuthCloudClient(network, NullLogger<OAuthCloudClient>.Instance);
        SetToken(cloud, "stale-access");

        using var cts = new CancellationTokenSource();
        var launchedAuthorize = false;
        var previousLauncher = BetterBclLauncher.Shared;
        var launcher = Substitute.For<ILauncher>();
        launcher.LaunchUriAsync(Arg.Any<Uri>()).Returns(call =>
        {
            var uri = call.Arg<Uri>();
            if (uri.AbsolutePath == "/api/auth/oauth2/authorize")
            {
                launchedAuthorize = true;
                cts.Cancel();
            }

            return Task.FromResult(true);
        });

        try
        {
            BetterBclLauncher.Shared = launcher;
            await cloud.LoginAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            Assert.Multiple(() =>
            {
                Assert.That(launchedAuthorize, Is.True);
                Assert.That(network.ProfilesRead, Is.EqualTo(1));
                Assert.That(network.TokenRequests, Is.EqualTo(1));
                Assert.That(HasRefreshToken(cloud), Is.False);
                secrets.Store.Received(1).DeleteSecret("com.sylinko.everywhere", "oauth_token_data");
            });
        }
        finally
        {
            BetterBclLauncher.Shared = previousLauncher;
        }
    }

    [AvaloniaTest]
    public async Task Login_WhenRefreshedTokenStillRejectedByUserInfo_ClearsSessionAndStartsInteractiveLogin()
    {
        using var secrets = new SecretStoreScope();
        using var network = new AccountServer
        {
            OnUserInfo = _ => InvalidTokenResponse(),
            OnToken = _ => TokenSuccess("fresh-refresh", "fresh-access", "fresh-id")
        };
        var cloud = new OAuthCloudClient(network, NullLogger<OAuthCloudClient>.Instance);
        SetToken(cloud, "stale-access");

        using var cts = new CancellationTokenSource();
        var launchedAuthorize = false;
        var previousLauncher = BetterBclLauncher.Shared;
        var launcher = Substitute.For<ILauncher>();
        launcher.LaunchUriAsync(Arg.Any<Uri>()).Returns(call =>
        {
            var uri = call.Arg<Uri>();
            if (uri.AbsolutePath == "/api/auth/oauth2/authorize")
            {
                launchedAuthorize = true;
                cts.Cancel();
            }

            return Task.FromResult(true);
        });

        try
        {
            BetterBclLauncher.Shared = launcher;
            await cloud.LoginAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            Assert.Multiple(() =>
            {
                Assert.That(launchedAuthorize, Is.True);
                Assert.That(network.ProfilesRead, Is.EqualTo(2));
                Assert.That(network.TokenRequests, Is.EqualTo(1));
                Assert.That(HasRefreshToken(cloud), Is.False);
                secrets.Store.Received(1).DeleteSecret("com.sylinko.everywhere", "oauth_token_data");
            });
        }
        finally
        {
            BetterBclLauncher.Shared = previousLauncher;
        }
    }

    [AvaloniaTest]
    public async Task Login_WhenUserInfoReturnsInvalidTokenAndRefreshFailsWithTransientError_PreservesStoredSession()
    {
        using var secrets = new SecretStoreScope();
        using var network = new AccountServer
        {
            OnUserInfo = _ => InvalidTokenResponse(),
            OnToken = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("temporarily unavailable", Encoding.UTF8, "text/plain")
            }
        };
        var cloud = new OAuthCloudClient(network, NullLogger<OAuthCloudClient>.Instance);
        SetToken(cloud, "stale-access");

        var previousLauncher = BetterBclLauncher.Shared;
        var launcher = Substitute.For<ILauncher>();
        launcher.LaunchUriAsync(Arg.Any<Uri>()).Returns(Task.FromResult(true));

        try
        {
            BetterBclLauncher.Shared = launcher;
            await cloud.LoginAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            Assert.Multiple(() =>
            {
                Assert.That(cloud.LoginStatus, Is.EqualTo(CloudClientLoginStatus.LoginFailed));
                Assert.That(cloud.LastLoginErrorKey, Is.Not.Null);
                Assert.That(network.ProfilesRead, Is.EqualTo(1));
                Assert.That(network.TokenRequests, Is.EqualTo(1));
                Assert.That(HasRefreshToken(cloud), Is.True);
                secrets.Store.DidNotReceive().DeleteSecret(Arg.Any<string>(), Arg.Any<string>());
            });
            await launcher.DidNotReceive().LaunchUriAsync(Arg.Any<Uri>());
        }
        finally
        {
            BetterBclLauncher.Shared = previousLauncher;
        }
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

    private static bool HasRefreshToken(OAuthCloudClient cloud)
    {
        var session = Field(cloud, "_session");
        var property = session.GetType().GetProperty("HasRefreshToken", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(session.GetType().Name, "HasRefreshToken");
        return property.GetValue(session) is true;
    }

    private static HttpResponseMessage InvalidTokenResponse() =>
        new(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"error":"invalid_token","error_description":"Invalid access token"}""",
                Encoding.UTF8,
                "application/json")
        };

    private static HttpResponseMessage UserInfoSuccess(string? account) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"sub\":\"{account}\",\"name\":\"Test\",\"email\":\"test@example.invalid\"}}",
                Encoding.UTF8,
                "application/json")
        };

    private static HttpResponseMessage TokenSuccess(string refreshToken, string accessToken, string idToken)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"refresh_token":"{{refreshToken}}","access_token":"{{accessToken}}","id_token":"{{idToken}}","expires_at":{{expiresAt}}}""",
                Encoding.UTF8,
                "application/json")
        };
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
                Encoding.UTF8, "application/json")
        };
    }

    private sealed class AccountServer : HttpMessageHandler, IHttpClientFactory
    {
        public int ProfilesRead { get; private set; }
        public int TokenRequests { get; private set; }
        public Func<string?, HttpResponseMessage>? OnUserInfo { get; init; }
        public Func<HttpRequestMessage, HttpResponseMessage>? OnToken { get; init; }

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
                return Task.FromResult(OnUserInfo is not null ? OnUserInfo(account) : UserInfoSuccess(account));
            }

            if (path == "/api/auth/oauth2/token")
            {
                TokenRequests++;
                if (OnToken is not null)
                {
                    return Task.FromResult(OnToken(request));
                }
            }

            Assert.That(path, Is.EqualTo("/api/subscription"), "No vault, OAuth token or external service requests are allowed.");
            var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Writer.TryWrite((reply, cancellationToken));
            return reply.Task;
        }
    }
}