using System.Net.Http.Headers;
using System.Net.Http.Json;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Extensions;
using Microsoft.Extensions.Logging;

namespace Everywhere.Cloud;

partial class OAuthCloudClient
{
    // Request ownership and publication are UI-thread-affine. Subscription waits do not hold
    // the login gate; replacing this source invalidates even a late, uncancellable reply.
    private CancellationTokenSource? _subscriptionCancellation;
    private Task _subscriptionTask = Task.CompletedTask;

    private async Task CompleteLoginAsync(bool isRestoredSession, CancellationToken cancellationToken)
    {
        var profile = await ReadUserProfileAsync(true, cancellationToken);
        if (profile.Subject is not { Length: > 0 } accountId)
        {
            throw new InvalidDataException("The userinfo response did not contain an account subject.");
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResetUserData();
            UserProfile = profile;
            // Publish startup ownership before LoggedIn allows sync to adopt the legacy account preference.
            RestoredAccountId = isRestoredSession ? accountId : null;
            LastLoginErrorKey = null;
            StartSubscriptionRefresh(cancellationToken).Detach(_logger.ToExceptionHandler());
            LoginStatus = CloudClientLoginStatus.LoggedIn;
        });
    }

    private async Task<UserProfile> ReadUserProfileAsync(bool shouldThrowOnError, CancellationToken cancellationToken)
    {
        var tokenData = await _session.GetValidTokenDataAsync(shouldThrowOnError, cancellationToken);
        if (string.IsNullOrEmpty(tokenData?.AccessToken))
        {
            throw new UserNotLoginException("Cannot refresh user profile without an access token.");
        }

        using var http = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        // Better Auth userinfo requires the opaque access token, not the ID token used by service APIs.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserProfile>(UserProfileJsonSerializerContext.Default.Options, cancellationToken)
            ?? throw new InvalidDataException("The userinfo response did not contain a profile.");
    }

    async Task ICloudClient.ReloadUserDataAsync(CancellationToken cancellationToken)
    {
        if (!await _userDataLock.WaitAsync(0, cancellationToken))
        {
            return;
        }
        try
        {
            // Opening the flyout during initial subscription loading joins that request.
            // Cancelling the flyout's wait does not cancel the login-owned refresh.
            Task? pending = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                pending = IsSubscriptionLoading ? _subscriptionTask : null;
            });
            if (pending is not null)
            {
                await pending.WaitAsync(cancellationToken);
                return;
            }
            var previous = UserProfile;
            if (previous is null || LoginStatus != CloudClientLoginStatus.LoggedIn)
            {
                return;
            }
            var profile = await ReadUserProfileAsync(false, cancellationToken);
            var refresh = Task.CompletedTask;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested || !ReferenceEquals(UserProfile, previous) ||
                    profile.Subject is not { } accountId || accountId != previous.Subject)
                {
                    return;
                }
                UserProfile = profile;
                refresh = StartSubscriptionRefresh(cancellationToken);
            });
            await refresh;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Failed to refresh user profile.");
        }
        finally
        {
            _userDataLock.Release();
        }
    }

    private Task StartSubscriptionRefresh(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _subscriptionCancellation = source;
        OnPropertyChanged(nameof(IsSubscriptionLoading));
        _subscriptionTask = ReadSubscriptionAsync(source);
        return _subscriptionTask;
    }

    private async Task ReadSubscriptionAsync(CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            using var http = _httpClientFactory.CreateClient(nameof(ICloudClient));
            using var response = await http.GetAsync(SubscriptionEndpoint, token);
            var payload = await ApiPayload<SubscriptionInformation>.EnsureSuccessFromHttpResponseJsonAsync(
                response, SubscriptionInformationJsonSerializerContext.Default.ApiPayloadSubscriptionInformation.Options, token);
            var subscription = payload.EnsureData();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_subscriptionCancellation, source) && !token.IsCancellationRequested)
                {
                    Subscription = subscription;
                    SubscriptionErrorKey = null;
                    UpdateNotifications();
                }
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Failed to refresh subscription.");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_subscriptionCancellation, source) && !token.IsCancellationRequested)
                {
                    SubscriptionErrorKey = HandledSystemException.Handle(error).GetFriendlyMessage();
                }
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_subscriptionCancellation, source))
                {
                    _subscriptionCancellation = null;
                    OnPropertyChanged(nameof(IsSubscriptionLoading));
                }
            });
            source.Dispose();
        }
    }

    private void ResetUserData()
    {
        var previous = _subscriptionCancellation;
        _subscriptionCancellation = null;
        // Invalidate publication before cancellation. The request's finally owns disposal.
        previous?.Cancel();
        OnPropertyChanged(nameof(IsSubscriptionLoading));
        UserProfile = null;
        RestoredAccountId = null;
        Subscription = null;
        SubscriptionErrorKey = null;
        UpdateNotifications();
    }
}