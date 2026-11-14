using System.ComponentModel;
using Everywhere.Collections;
using Everywhere.Common.Notification;

namespace Everywhere.Cloud;

public enum CloudClientLoginStatus
{
    NotLoggedIn,
    LoggedIn,
    AutoLoggingIn,
    LoginFailed,
    /// <summary>
    /// Account endpoints or the OAuth client ID were not supplied when building the app.
    /// </summary>
    NotConfigured
}

/// <summary>
/// Interface for cloud client operations, handling authentication and user profile management.
/// Implements <see cref="INotifyPropertyChanged"/> to support data binding for the <see cref="UserProfile"/> property.
/// </summary>
public interface ICloudClient : INotifyPropertyChanged
{
    /// <summary>
    /// Gets the current logged-in user profile. Returns null if not logged in.
    /// This property raises <see cref="INotifyPropertyChanged.PropertyChanged"/> when updated.
    /// </summary>
    UserProfile? UserProfile { get; }

    /// <summary>
    /// Account authenticated by restoring the pre-existing device session at startup, not by an interactive login.
    /// </summary>
    string? RestoredAccountId { get; }

    /// <summary>
    /// Gets the current subscription information for the logged-in user. Returns null if not logged in or if subscription information is unavailable.
    /// This property raises <see cref="INotifyPropertyChanged.PropertyChanged"/> when updated.
    /// </summary>
    SubscriptionInformation? Subscription { get; }

    /// <summary>
    /// Whether subscription data is being refreshed independently of authentication.
    /// </summary>
    bool IsSubscriptionLoading { get; }

    /// <summary>
    /// Localized subscription read failure, without invalidating an authenticated profile.
    /// </summary>
    IDynamicLocaleKey? SubscriptionErrorKey { get; }

    /// <summary>
    /// Gets the current login status of the cloud client, indicating whether the user is logged in, logging in, or if a login attempt has failed.
    /// </summary>
    CloudClientLoginStatus LoginStatus { get; }

    /// <summary>
    /// Gets the resource key for the last error message encountered during authentication. Subscription read failures are reported separately. Returns null if there are no errors.
    /// </summary>
    IDynamicLocaleKey? LastLoginErrorKey { get; }

    /// <summary>
    /// Gets a list of notifications
    /// </summary>
    IReadOnlyBindableList<DynamicNotification> Notifications { get; }

    /// <summary>
    /// Initiates the OAuth 2.0 (PKCE) login flow.
    /// This process should handle browser interaction, callback capture, token exchange, and initial user profile retrieval.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>A task returning true if login was successful, otherwise false.</returns>
    Task LoginAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Logs out the current user, revoking tokens and clearing local storage.
    /// </summary>
    Task LogoutAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Refreshes the authenticated profile and subscription, reusing any subscription request already in progress.
    /// Cancelling this wait does not cancel a subscription request started by login.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ReloadUserDataAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates a DelegatingHandler that can be added to the HTTP client pipeline to automatically handle authentication.
    /// It adds the necessary Authorization header to outgoing requests and attempts token refresh on 401 responses.
    /// </summary>
    /// <remarks>
    /// Request content must support repeated serialization. The handler does not buffer or own it.
    /// Callers with a single-use input stream must provide a replayable source before using this handler.
    /// </remarks>
    /// <returns></returns>
    DelegatingHandler CreateAuthenticationHandler();
}

/// <summary>
/// Exception thrown when an operation requires the user to be logged in, but they are not.
/// Derived from <see cref="OperationCanceledException"/> to allow it to be used in cancellation scenarios without being treated as an error.
/// </summary>
public sealed class UserNotLoginException : OperationCanceledException
{
    public UserNotLoginException() { }

    public UserNotLoginException(string message) : base(message) { }
}