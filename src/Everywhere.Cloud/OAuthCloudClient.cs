using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Extensions;
using Everywhere.Messages;
using GnomeStack.Os.Secrets;
using Microsoft.Extensions.Logging;

namespace Everywhere.Cloud;

public sealed partial class OAuthCloudClient :
    ObservableObject,
    ICloudClient,
    IAsyncInitializer,
    IRecipient<ApplicationMessage>
{
    private const string ServiceName = "com.sylinko.everywhere";
    private const string TokenDataKey = "oauth_token_data";

    private const string AuthorizeEndpoint = $"{CloudConstants.AccountBaseUrl}/api/auth/oauth2/authorize";
    private const string TokenEndpoint = $"{CloudConstants.AccountBaseUrl}/api/auth/oauth2/token";
    private const string UserInfoEndpoint = $"{CloudConstants.AccountBaseUrl}/api/auth/oauth2/userinfo";
    private const string RevokeEndpoint = $"{CloudConstants.AccountBaseUrl}/api/auth/oauth2/revoke";
    private const string SubscriptionEndpoint = $"{CloudConstants.AccountBaseUrl}/api/subscription";
    private const string RedirectUri = "sylinko-everywhere://callback";
    private const string Scopes = "openid profile email offline_access";

    [ObservableProperty]
    public partial UserProfile? UserProfile { get; private set; }

    [ObservableProperty]
    public partial SubscriptionInformation? Subscription { get; private set; }

    [ObservableProperty]
    public partial CloudClientLoginStatus LoginStatus { get; private set; }

    [ObservableProperty]
    public partial IDynamicResourceKey? LastLoginErrorKey { get; private set; }

    public IReadOnlyBindableList<DynamicNotification> Notifications => _notificationManager.Notifications;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OAuthCloudClient> _logger;

    // Core state management extracted into inner contexts
    private readonly TokenSessionContext _session;
    private volatile InteractiveOAuthFlow? _activeAuthFlow;

    private readonly DynamicNotificationManager _notificationManager = new(new InMemoryKeyValueStorage());
    private readonly SemaphoreSlim _loginLock = new(1, 1); // Concurrency control for the UI entry point to prevent multiple login windows
    private readonly SemaphoreSlim _userDataLock = new(1, 1); // Prevent concurrent user data refreshes
    private readonly Lock _initializeTaskGate = new();
    private Task? _initializeTask;

    public OAuthCloudClient(IHttpClientFactory httpClientFactory, ILogger<OAuthCloudClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _session = new TokenSessionContext(httpClientFactory, logger, HandleSessionInvalidated);
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    public async Task LoginAsync(CancellationToken cancellationToken)
    {
        // Coordinate with silent login instead of racing it.
        await _loginLock.WaitAsync(cancellationToken);

        try
        {
            LastLoginErrorKey = null;

            if (await TryGetExistingSessionTokenForLoginAsync(cancellationToken) != null)
            {
                LoginStatus = CloudClientLoginStatus.AutoLoggingIn;
                await ReloadUserDataAsync(true, cancellationToken);
                LoginStatus = CloudClientLoginStatus.LoggedIn;
                LastLoginErrorKey = null;
                return;
            }

            LoginStatus = CloudClientLoginStatus.NotLoggedIn;
            using var flow = new InteractiveOAuthFlow(_httpClientFactory, cancellationToken);
            _activeAuthFlow = flow; // Expose to the message receiver for URL callbacks

            var authorizeUrl = flow.BuildAuthorizeUrl();
            _logger.LogDebug("Starting login flow. Auth URL: {AuthorizeUrl}", authorizeUrl);
            await App.Launcher.LaunchUriAsync(new Uri(authorizeUrl));

            // Wait for the OS protocol callback or timeout (managed entirely within the flow context)
            var code = await flow.WaitForCode();

            LoginStatus = CloudClientLoginStatus.AutoLoggingIn;
            var tokenData = await flow.ExchangeCodeAsync(code);
            _session.SetToken(tokenData);
            await ReloadUserDataAsync(true, cancellationToken);

            LoginStatus = CloudClientLoginStatus.LoggedIn;
            LastLoginErrorKey = null;
        }
        catch (OperationCanceledException)
        {
            try
            {
                await LogoutCoreAsync(false, cancellationToken);
            }
            catch
            {
                // Ignore
            }
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            _logger.LogError(ex, "Failed to complete login flow.");
            // Don't clear the existing session here - the refresh token from a previous
            // successful login may still be valid. Clearing it would force the user to
            // re-authenticate even if only the interactive flow failed (e.g., timeout,
            // browser error, network glitch during the new flow).

            LoginStatus = CloudClientLoginStatus.LoginFailed;
            LastLoginErrorKey = ex.GetFriendlyMessage();
        }
        finally
        {
            _activeAuthFlow = null; // Clean up the transient flow context
            _loginLock.Release();
        }
    }

    private async Task<TokenData?> TryGetExistingSessionTokenForLoginAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _session.GetValidTokenDataAsync(true, cancellationToken);
        }
        catch (OAuthTokenRequestException ex) when (ex.IsRefreshTokenRejected)
        {
            _logger.LogInformation(
                ex,
                "Stored OAuth refresh token was rejected by the server. Clearing local session before interactive login.");
            return null;
        }
    }

    public Task LogoutAsync(CancellationToken cancellationToken) => LogoutCoreAsync(true, cancellationToken);

    private async Task LogoutCoreAsync(bool acquireLock, CancellationToken cancellationToken)
    {
        if (acquireLock) await _loginLock.WaitAsync(cancellationToken);

        try
        {
            await _session.RevokeAndClearAsync(cancellationToken);
            UserProfile = null;
            Subscription = null;
            UpdateNotifications();
            LastLoginErrorKey = null;
        }
        finally
        {
            LoginStatus = CloudClientLoginStatus.NotLoggedIn;
            if (acquireLock) _loginLock.Release();
        }
    }

    private void HandleSessionInvalidated(IDynamicResourceKey errorKey)
    {
        UserProfile = null;
        Subscription = null;
        UpdateNotifications();
        LoginStatus = CloudClientLoginStatus.LoginFailed;
        LastLoginErrorKey = errorKey;
    }

    Task ICloudClient.ReloadUserDataAsync(CancellationToken cancellationToken) => ReloadUserDataAsync(false, cancellationToken);

    private async Task ReloadUserDataAsync(bool throwOnError, CancellationToken cancellationToken)
    {
        if (!await _userDataLock.WaitAsync(0, cancellationToken)) return; // Prevent concurrent refreshes

        try
        {
            // Ensure we have valid tokens, triggering a refresh if necessary.
            // According to BetterAuth docs, /userinfo needs to be authenticated with the Access Token (Opaque), not the ID token (JWT).
            var tokenData = await _session.GetValidTokenDataAsync(throwOnError, cancellationToken);
            var accessToken = tokenData?.AccessToken;

            if (string.IsNullOrEmpty(accessToken))
            {
                throw new UserNotLoginException("Cannot refresh user profile without an access token.");
            }

            using var httpClient = _httpClientFactory.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            UserProfile = await response.Content.ReadFromJsonAsync<UserProfile>(
                UserProfileJsonSerializerContext.Default.Options,
                cancellationToken: cancellationToken);

            // The subscription info is not included in the user profile response, so we need a separate call.
            await RefreshSubscriptionAsync(cancellationToken);

            // Add a 3 second debounce
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (OperationCanceledException) when (!throwOnError)
        {
            // ignore
        }
        catch (Exception ex) when (!throwOnError)
        {
            ex = HandledSystemException.Handle(ex);
            _logger.LogError(ex, "Failed to refresh user profile.");
        }
        finally
        {
            _userDataLock.Release();
        }
    }

    private async Task RefreshSubscriptionAsync(CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient(nameof(ICloudClient));

        var request = new HttpRequestMessage(HttpMethod.Get, SubscriptionEndpoint);
        var response = await httpClient.SendAsync(request, cancellationToken);

        var payload = await ApiPayload<SubscriptionInformation>.EnsureSuccessFromHttpResponseJsonAsync(
            response,
            SubscriptionInformationJsonSerializerContext.Default.ApiPayloadSubscriptionInformation.Options,
            cancellationToken);

        Subscription = payload.EnsureData();
        UpdateNotifications();
    }

    private void UpdateNotifications()
    {
        if (Subscription is not { } subscription)
        {
            _notificationManager.Clear();
            return;
        }

        if (subscription.Plan == SubscriptionPlan.Banned)
        {
            DynamicNotificationDescriptor[] notifications =
            [
                new(
                    "banned",
                    new DynamicResourceKey(LocaleKey.OAuthCloudClient_BannedNotification_Title),
                    NotificationType.Error,
                    false)
            ];
            _notificationManager.Reset(notifications);
            return;
        }

        var generatedNotifications = GenerateNotifications().ToList();
        _notificationManager.Reset(generatedNotifications);

        IEnumerable<DynamicNotificationDescriptor> GenerateNotifications()
        {
            if (subscription.Status == SubscriptionStatus.Unpaid)
            {
                yield return new DynamicNotificationDescriptor(
                    "unpaid",
                    new DynamicResourceKey(LocaleKey.OAuthCloudClient_UnpaidNotification_Title),
                    NotificationType.Error,
                    false);
            }

            if (subscription.Plan != SubscriptionPlan.Free)
            {
                if (subscription.PeriodEnd is { } periodEnd && (periodEnd - DateTimeOffset.UtcNow).TotalDays > 7)
                {
                    if (subscription is { BonusCredits: < 10000, RemainingPlanCreditsRatio: > 0.01d and < 0.2d })
                    {
                        yield return new DynamicNotificationDescriptor(
                            "credits_running_low",
                            new DynamicResourceKey(LocaleKey.OAuthCloudClient_CreditsRunningLowNotification_Content),
                            NotificationType.Warning);
                    }

                    if (subscription.RemainingFreeWebSearchRatio is > 0.01d and < 0.2d)
                    {
                        yield return new DynamicNotificationDescriptor(
                            "free_web_search_running_low",
                            new DynamicResourceKey(LocaleKey.OAuthCloudClient_FreeWebSearchRunningLowNotification_Content),
                            NotificationType.Warning);
                    }
                }

                if (subscription.RemainingFreeWebSearchRatio <= 0d)
                {
                    yield return new DynamicNotificationDescriptor(
                        "free_web_search_depleted",
                        new DynamicResourceKey(LocaleKey.OAuthCloudClient_FreeWebSearchDepletedNotification_Content),
                        NotificationType.Error);
                }
            }
        }
    }

    public DelegatingHandler CreateAuthenticationHandler() => new CloudAuthenticationHandler(_session, _logger);

    void IRecipient<ApplicationMessage>.Receive(ApplicationMessage message)
    {
        if (message is not UrlProtocolCallbackMessage oauth) return;

        _logger.LogDebug("Received URL callback: {url}", oauth.Url);

        // Route the callback to the active interactive flow if one is currently awaiting
        _activeAuthFlow?.HandleCallback(oauth.Url);
    }

    #region IAsyncInitializer Implementation

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Network + 1;

    /// <summary>
    /// Initializes the client by attempting a silent login using stored tokens.
    /// This allows the app to restore the user's session without requiring them to log in again.
    /// </summary>
    public Task InitializeAsync()
    {
        // Fire and forget the initialization to avoid blocking app startup.
        lock (_initializeTaskGate)
        {
            _initializeTask ??= InitializeCoreAsync();
            _initializeTask.Detach(_logger.ToExceptionHandler());
        }

        return Task.CompletedTask;
    }

    private async Task InitializeCoreAsync()
    {
        if (!await _loginLock.WaitAsync(0)) return; // Prevent reentry for initialization

        try
        {
            LoginStatus = CloudClientLoginStatus.AutoLoggingIn;
            LastLoginErrorKey = null;
            _session.LoadFromVault();

            // Try to refresh token and restore user data silently if a refresh token exists
            if (_session.HasRefreshToken)
            {
                TokenData? tokenData;
                try
                {
                    tokenData = await _session.GetValidTokenDataAsync(true, CancellationToken.None);
                }
                catch (OAuthTokenRequestException ex) when (ex.IsRefreshTokenRejected)
                {
                    _logger.LogInformation(
                        ex,
                        "Stored OAuth refresh token was rejected during silent login. Clearing local session.");
                    return;
                }

                if (tokenData is not null)
                {
                    await ReloadUserDataAsync(true, CancellationToken.None);
                    LoginStatus = CloudClientLoginStatus.LoggedIn;
                    LastLoginErrorKey = null;
                    return;
                }
            }

            LoginStatus = CloudClientLoginStatus.NotLoggedIn;
            LastLoginErrorKey = null;
        }
        catch (Exception ex)
        {
            // Expected if the user has never logged in or if stored tokens are invalid/expired.
            ex = HandledSystemException.Handle(ex);
            _logger.LogInformation(ex, "Silent login failed during initialization.");

            if (LoginStatus != CloudClientLoginStatus.LoginFailed)
            {
                LoginStatus = CloudClientLoginStatus.LoginFailed;
                LastLoginErrorKey = ex.GetFriendlyMessage();
            }
        }
        finally
        {
            _loginLock.Release();
        }
    }

    #endregion

    /// <summary>
    /// Record for holding token data for secure storage. Using a record for easy JSON serialization and immutability.
    /// </summary>
    private partial record TokenData(
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("access_token")] string? AccessToken = null,
        [property: JsonPropertyName("id_token")] string? IdToken = null,
        [property: JsonPropertyName("expires_at")] long ExpiresAtTimestamp = 0
    )
    {
        /// <summary>
        /// Checks if the token is expired or will expire within the next 10 seconds.
        /// </summary>
        public bool IsTokenExpiredOrNearingExpiry => ExpiresAtTimestamp < DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 10;

        [JsonSerializable(typeof(TokenData))]
        public partial class TokenDataJsonSerializerContext : JsonSerializerContext;

        public static TokenData? FromJson(string json)
        {
            try
            {
                return JsonSerializer.Deserialize(json, TokenDataJsonSerializerContext.Default.TokenData);
            }
            catch
            {
                return null;
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this, TokenDataJsonSerializerContext.Default.TokenData);
    }

    private sealed partial record OAuthErrorResponse(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription = null,
        [property: JsonPropertyName("error_uri")] string? ErrorUri = null
    )
    {
        [JsonSerializable(typeof(OAuthErrorResponse))]
        public partial class OAuthErrorResponseJsonSerializerContext : JsonSerializerContext;

        public static OAuthErrorResponse? FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                return JsonSerializer.Deserialize(json, OAuthErrorResponseJsonSerializerContext.Default.OAuthErrorResponse);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private sealed class OAuthTokenRequestException : HandledException
    {
        public string Error { get; }
        public string ErrorDescription { get; }

        public bool IsRefreshTokenRejected => Error is "invalid_grant" or "invalid_token";

        private OAuthTokenRequestException(
            HttpStatusCode statusCode,
            string error,
            string errorDescription,
            string? errorUri
        ) : base(
            new HttpRequestException(
                FormatExceptionMessage(statusCode, error, errorDescription, errorUri),
                null,
                statusCode),
            CreateFriendlyMessageKey(statusCode, error, errorDescription, errorUri),
            showDetails: false)
        {
            Error = error;
            ErrorDescription = errorDescription;
        }

        public static OAuthTokenRequestException FromResponse(HttpStatusCode statusCode, string responseContent)
        {
            var errorResponse = OAuthErrorResponse.FromJson(responseContent);
            var error = Normalize(errorResponse?.Error) ?? $"http_{(int)statusCode}";
            var errorDescription =
                Normalize(errorResponse?.ErrorDescription) ??
                NormalizeNonJsonResponse(responseContent) ??
                statusCode.ToString();
            var errorUri = Normalize(errorResponse?.ErrorUri);

            return new OAuthTokenRequestException(statusCode, error, errorDescription, errorUri);
        }

        private static IDynamicResourceKey CreateFriendlyMessageKey(
            HttpStatusCode statusCode,
            string error,
            string errorDescription,
            string? errorUri)
        {
            var key = error switch
            {
                "invalid_grant" or "invalid_token" => LocaleKey.OAuthCloudClient_TokenInvalidGrant,
                "invalid_client" => LocaleKey.OAuthCloudClient_TokenInvalidClient,
                "server_error" or "temporarily_unavailable" => LocaleKey.OAuthCloudClient_TokenEndpointUnavailable,
                _ when (int)statusCode >= 500 => LocaleKey.OAuthCloudClient_TokenEndpointUnavailable,
                _ => LocaleKey.OAuthCloudClient_TokenRejected
            };

            return new FormattedDynamicResourceKey(
                key,
                new DirectResourceKey(error),
                new DirectResourceKey(FormatErrorDescription(errorDescription, errorUri)));
        }

        private static string FormatExceptionMessage(
            HttpStatusCode statusCode,
            string error,
            string errorDescription,
            string? errorUri)
        {
            var message = $"OAuth token request failed with {(int)statusCode} {statusCode}: {error}. {errorDescription}";
            return string.IsNullOrWhiteSpace(errorUri) ? message : $"{message} {errorUri}";
        }

        private static string FormatErrorDescription(string errorDescription, string? errorUri) =>
            string.IsNullOrWhiteSpace(errorUri) ? errorDescription : $"{errorDescription} ({errorUri})";

        private static string? Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            return value.Length <= 500 ? value : value[..500];
        }

        private static string? NormalizeNonJsonResponse(string responseContent)
        {
            var normalized = Normalize(responseContent);
            return normalized?.ReplaceLineEndings(" ");
        }
    }

    /// <summary>
    /// Context responsible for the transient, interactive OAuth PKCE flow.
    /// Manages timeouts, state validation, and exchanging the authorization code.
    /// </summary>
    private sealed class InteractiveOAuthFlow : IDisposable
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _expectedState;
        private readonly string _codeVerifier;
        private readonly string _codeChallenge;
        private readonly TaskCompletionSource<string> _authCodeTcs;
        private readonly CancellationTokenSource _timeoutCts;
        private readonly CancellationTokenRegistration _ctr;

        public InteractiveOAuthFlow(IHttpClientFactory httpClientFactory, CancellationToken externalToken)
        {
            _httpClientFactory = httpClientFactory;
            _expectedState = Guid.NewGuid().ToString();
            _codeVerifier = GenerateCodeVerifier();
            _codeChallenge = GenerateCodeChallenge(_codeVerifier);
            _authCodeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Limit the wait time for user interaction to 30 minutes to prevent memory leaks
            _timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            _timeoutCts.CancelAfter(TimeSpan.FromMinutes(30));
            _ctr = _timeoutCts.Token.Register(() => _authCodeTcs.TrySetCanceled());
        }

        public string BuildAuthorizeUrl()
        {
            var sb = new StringBuilder(AuthorizeEndpoint);
            sb.Append("?response_type=code");
            sb.Append($"&client_id={CloudConstants.ClientId}");
            sb.Append($"&redirect_uri={Uri.EscapeDataString(RedirectUri)}");
            sb.Append($"&state={_expectedState}");
            sb.Append($"&scope={Uri.EscapeDataString(Scopes)}");
            sb.Append($"&code_challenge={Uri.EscapeDataString(_codeChallenge)}");
            sb.Append("&code_challenge_method=S256");
            return sb.ToString();
        }

        public void HandleCallback(string url)
        {
            if (_authCodeTcs.Task.IsCompleted) return;

            try
            {
                var uri = new Uri(url);
                var query = HttpUtility.ParseQueryString(uri.Query);
                var code = query["code"];
                var state = query["state"];
                var error = query["error"];
                var errorDescription = query["error_description"];

                if (!string.IsNullOrEmpty(error))
                {
                    // _authCodeTcs.TrySetException(new InvalidDataException($"OAuth Error: {error} - {errorDescription}"));
                    _authCodeTcs.TrySetException(
                        new HandledSystemException(
                            new InvalidDataException($"OAuth Error: {error} - {errorDescription}"),
                            HandledSystemExceptionType.InvalidData,
                            new FormattedDynamicResourceKey(
                                LocaleKey.OAuthCloudClient_OAuthError,
                                new DirectResourceKey(error),
                                new DynamicResourceKey(errorDescription))));
                    return;
                }

                if (state != _expectedState)
                {
                    // _authCodeTcs.TrySetException(new InvalidDataException($"Invalid state received. Expected: {_expectedState}, Received: {state}"));
                    _authCodeTcs.TrySetException(
                        new HandledSystemException(
                            new InvalidDataException($"Invalid state received. Expected: {_expectedState}, Received: {state}"),
                            HandledSystemExceptionType.InvalidData,
                            new DynamicResourceKey(LocaleKey.OAuthCloudClient_InvalidState)));
                    return;
                }

                if (string.IsNullOrEmpty(code))
                {
                    // _authCodeTcs.TrySetException(new InvalidDataException("No code found in callback."));
                    _authCodeTcs.TrySetException(
                        new HandledSystemException(
                            new InvalidDataException("No code found in callback."),
                            HandledSystemExceptionType.InvalidData,
                            new DynamicResourceKey(LocaleKey.OAuthCloudClient_MissingCode)));
                    return;
                }

                _authCodeTcs.TrySetResult(code);
            }
            catch (Exception ex)
            {
                _authCodeTcs.TrySetException(ex);
            }
        }

        public Task<string> WaitForCode() => _authCodeTcs.Task;

        public async Task<TokenData> ExchangeCodeAsync(string code)
        {
            var parameters = new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", code },
                { "redirect_uri", RedirectUri },
                { "client_id", CloudConstants.ClientId },
                { "code_verifier", _codeVerifier }
            };

            return await RequestTokenInternalAsync(_httpClientFactory, parameters, _timeoutCts.Token);
        }

        public void Dispose()
        {
            _ctr.Dispose();
            _timeoutCts.Dispose();
        }

        // PKCE Helpers
        private static string GenerateCodeVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        private static unsafe string GenerateCodeChallenge(string codeVerifier)
        {
            Span<byte> verifierBytes = stackalloc byte[codeVerifier.Length];
            var bytesWritten = Encoding.ASCII.GetBytes(codeVerifier, verifierBytes);
            verifierBytes = verifierBytes[..bytesWritten];
            Span<byte> hashBytes = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(verifierBytes, hashBytes);
            return Base64Url.EncodeToString(hashBytes);
        }
    }

    /// <summary>
    /// Context responsible for long-lived session state, token persistence, and concurrency control for refreshing.
    /// Exposes methods to retrieve valid tokens without bleeding state logic to the main client.
    /// </summary>
    private sealed class TokenSessionContext(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        Action<IDynamicResourceKey> onSessionInvalidated
    )
    {
        private readonly Lock _stateGate = new();
        private TokenData? _tokenData;
        private long _generation;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        public bool HasRefreshToken
        {
            get
            {
                lock (_stateGate)
                {
                    return _tokenData is { RefreshToken.Length: > 0 };
                }
            }
        }

        public string? CurrentIdToken
        {
            get
            {
                lock (_stateGate)
                {
                    return _tokenData?.IdToken;
                }
            }
        }

        public void LoadFromVault()
        {
            lock (_stateGate)
            {
                try
                {
                    var json = OsSecretVault.GetSecret(ServiceName, TokenDataKey);
                    _tokenData = string.IsNullOrEmpty(json) ? null : TokenData.FromJson(json);
                }
                catch (Exception ex)
                {
                    _tokenData = null;
                    ex = HandledSystemException.Handle(ex);
                    logger.LogWarning(ex, "Failed to read token data from secure storage. Proceeding with empty session.");
                }

                _generation++;
            }
        }

        public void SetToken(TokenData data)
        {
            lock (_stateGate)
            {
                SaveTokenCore(data);
            }
        }

        private void SaveTokenCore(TokenData data)
        {
            _generation++;

            try
            {
                OsSecretVault.SetSecret(ServiceName, TokenDataKey, data.ToJson());
                _tokenData = data;
            }
            catch (Exception ex)
            {
                _tokenData = data;
                ex = HandledSystemException.Handle(ex);
                logger.LogWarning(
                    ex,
                    "Failed to save token data to secure storage. Clearing persisted token to avoid reusing a stale refresh token later.");

                try
                {
                    OsSecretVault.DeleteSecret(ServiceName, TokenDataKey);
                }
                catch (Exception deleteEx)
                {
                    deleteEx = HandledSystemException.Handle(deleteEx);
                    logger.LogWarning(deleteEx, "Failed to clear token data from secure storage after save failure");
                }
            }
        }

        private bool ClearLocalSessionCore(long? expectedGeneration, string? expectedRefreshToken)
        {
            lock (_stateGate)
            {
                if (expectedGeneration is { } generation && generation != _generation) return false;
                if (expectedRefreshToken is not null && _tokenData?.RefreshToken != expectedRefreshToken) return false;

                _generation++;
                _tokenData = null;
                try
                {
                    OsSecretVault.DeleteSecret(ServiceName, TokenDataKey);
                }
                catch (Exception ex)
                {
                    ex = HandledSystemException.Handle(ex);
                    logger.LogWarning(ex, "Failed to delete token data from secure storage");
                }

                return true;
            }
        }

        private bool ClearRejectedSession(long expectedGeneration, string expectedRefreshToken, IDynamicResourceKey errorKey)
        {
            var cleared = ClearLocalSessionCore(expectedGeneration, expectedRefreshToken);
            if (cleared) onSessionInvalidated(errorKey);
            return cleared;
        }

        /// <summary>
        /// Ensures the current token data is valid. If it's nearing expiry, automatically triggers a refresh.
        /// Returns the latest TokenData object, allowing callers to extract IdToken or AccessToken as needed.
        /// </summary>
        public async Task<TokenData?> GetValidTokenDataAsync(bool throwOnError, CancellationToken cancellationToken)
        {
            var snapshot = GetSnapshot();
            if (snapshot.TokenData is null) return null;

            // If token is still fresh, return it directly
            if (!snapshot.TokenData.IsTokenExpiredOrNearingExpiry) return snapshot.TokenData;

            // If it's expired or nearing expiry, attempt to refresh it
            var refreshed = await TryRefreshAsync(throwOnError, cancellationToken);
            return refreshed ? GetSnapshot().TokenData : null;
        }

        public async Task<bool> TryRefreshAsync(bool throwOnError, CancellationToken cancellationToken)
        {
            var snapshot = GetSnapshot();
            if (snapshot.TokenData is not { RefreshToken.Length: > 0 }) return false;

            // Prevent concurrent refresh attempts (Double-check locking pattern)
            if (!await _refreshLock.WaitAsync(0, cancellationToken))
            {
                await _refreshLock.WaitAsync(cancellationToken);
                _refreshLock.Release();

                // Another thread might have completed the refresh while we were waiting
                return GetSnapshot().TokenData is { IsTokenExpiredOrNearingExpiry: false };
            }

            long refreshGeneration = 0;
            string? refreshToken = null;

            try
            {
                snapshot = GetSnapshot();
                if (snapshot.TokenData is not { RefreshToken.Length: > 0 } tokenData) return false;

                refreshGeneration = snapshot.Generation;
                refreshToken = tokenData.RefreshToken;

                var parameters = new Dictionary<string, string>
                {
                    { "grant_type", "refresh_token" },
                    { "refresh_token", refreshToken },
                    { "client_id", CloudConstants.ClientId }
                };

                var newTokenData = await RequestTokenInternalAsync(httpClientFactory, parameters, cancellationToken);
                return CommitRefreshResult(refreshGeneration, refreshToken, newTokenData);
            }
            catch (OAuthTokenRequestException ex) when (ex.IsRefreshTokenRejected)
            {
                logger.LogWarning(
                    ex,
                    "Refresh token rejected by server. Clearing local session. OAuth error: {OAuthError}; description: {OAuthErrorDescription}",
                    ex.Error,
                    ex.ErrorDescription);

                var cleared = refreshToken is not null &&
                    ClearRejectedSession(refreshGeneration, refreshToken, ex.FriendlyMessageKey);

                if (throwOnError && cleared) throw;
                return false;
            }
            catch (HttpRequestException ex) when (!throwOnError && ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            {
                // Refresh token is explicitly rejected by the server (e.g., revoked, expired)
                logger.LogWarning(ex, "Refresh token rejected by server. Clearing local session.");
                if (refreshToken is not null)
                {
                    ClearRejectedSession(refreshGeneration, refreshToken, HandledSystemException.Handle(ex).GetFriendlyMessage());
                }

                return false;
            }
            catch (Exception ex) when (!throwOnError)
            {
                // Network errors or other transient issues. Keep the current session, it might recover later.
                ex = HandledSystemException.Handle(ex);
                logger.LogWarning(ex, "Token refresh failed due to network or unknown error.");
                return false;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private (TokenData? TokenData, long Generation) GetSnapshot()
        {
            lock (_stateGate)
            {
                return (_tokenData, _generation);
            }
        }

        private bool CommitRefreshResult(long expectedGeneration, string expectedRefreshToken, TokenData data)
        {
            lock (_stateGate)
            {
                if (_generation != expectedGeneration || _tokenData?.RefreshToken != expectedRefreshToken)
                {
                    logger.LogDebug("Discarding stale OAuth refresh result because the local session changed.");
                    return false;
                }

                SaveTokenCore(data);
                return true;
            }
        }

        public async Task RevokeAndClearAsync(CancellationToken cancellationToken)
        {
            var tokenData = GetSnapshot().TokenData;
            if (tokenData is null) return;

            using var httpClient = httpClientFactory.CreateClient();

            var accessToken = tokenData.AccessToken;
            var refreshToken = tokenData.RefreshToken;

            ClearLocalSessionCore(null, null); // Clear locally first to ensure UI reflects logout immediately

            if (!string.IsNullOrEmpty(accessToken)) await RevokeTokenInternalAsync(httpClient, accessToken, "access_token", cancellationToken);
            if (!string.IsNullOrEmpty(refreshToken)) await RevokeTokenInternalAsync(httpClient, refreshToken, "refresh_token", cancellationToken);
        }

        private async Task RevokeTokenInternalAsync(HttpClient client, string token, string tokenTypeHint, CancellationToken cancellationToken)
        {
            try
            {
                var parameters = new Dictionary<string, string>
                {
                    { "token", token },
                    { "token_type_hint", tokenTypeHint },
                    { "client_id", CloudConstants.ClientId }
                };
                var request = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint) { Content = new FormUrlEncodedContent(parameters) };
                await client.SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                ex = HandledSystemException.Handle(ex);
                logger.LogWarning(ex, "Failed to revoke remote {TokenType}", tokenTypeHint);
            }
        }
    }

    /// <summary>
    /// Shared HTTP logic for requesting tokens (used by both initial exchange and refresh).
    /// </summary>
    private static async Task<TokenData> RequestTokenInternalAsync(
        IHttpClientFactory factory,
        Dictionary<string, string> parameters,
        CancellationToken ct)
    {
        using var httpClient = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(parameters)
        };

        var response = await httpClient.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw OAuthTokenRequestException.FromResponse(response.StatusCode, content);
        }

        var tokenData = TokenData.FromJson(content);
        return tokenData ?? throw new HttpRequestException("Failed to parse token response. Invalid format.", null, response.StatusCode);
    }

    /// <summary>
    /// A delegating handler that automatically adds JWT authentication headers to outgoing requests
    /// and handles token refresh on 401 Unauthorized responses.
    /// </summary>
    /// <remarks>
    /// This handler MUST use the IdToken (JWT) for general API requests, as resource servers validate claims.
    /// </remarks>
    private sealed class CloudAuthenticationHandler(TokenSessionContext session, ILogger logger) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 1. Proactively get a valid token data and extract the IdToken (JWT)
            var tokenData = await session.GetValidTokenDataAsync(false, cancellationToken);
            var idToken = tokenData?.IdToken;

            if (string.IsNullOrEmpty(idToken))
            {
                throw new UserNotLoginException();
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
            request.Headers.Add("ew-device-id", RuntimeConstants.DeviceId);

            // Ensure content is buffered for potential retry
            if (request.Content != null)
            {
                await request.Content.LoadIntoBufferAsync(cancellationToken);
            }

            var retryRequest = await CloneRequestAsync(request, cancellationToken);
            var response = await base.SendAsync(request, cancellationToken);

            // If the response is not 401, return it as is
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                retryRequest.Dispose();
                return response;
            }

            logger.LogDebug("Received 401 Unauthorized, attempting to force refresh token...");

            // 2. If we get a 401, the server might have invalidated the token early. Force a refresh.
            var refreshed = await session.TryRefreshAsync(false, cancellationToken);
            if (!refreshed)
            {
                retryRequest.Dispose();
                return response;
            }

            // 3. Extract the new IdToken after successful refresh
            var newIdToken = session.CurrentIdToken;
            if (string.IsNullOrEmpty(newIdToken))
            {
                retryRequest.Dispose();
                return response;
            }

            // Dispose the original response before retrying
            response.Dispose();
            retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newIdToken);
            return await base.SendAsync(retryRequest, cancellationToken);
        }

        private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy
            };

            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is not null)
            {
                var content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                foreach (var header in request.Content.Headers)
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                clone.Content = content;
            }

            return clone;
        }
    }
}
