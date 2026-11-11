using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Database;
using Everywhere.Storage;
using Everywhere.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Everywhere.Cloud;

/// <summary>
/// Binds authentication and the account-specific device preference to one sync session.
/// The session owns all inventory, scheduling and participation commands.
/// </summary>
public sealed class ChatSyncService : ObservableObject, IChatSyncService, IAsyncInitializer, IAsyncDisposable
{
    /// <inheritdoc />
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Network + 2;

    /// <inheritdoc />
    public string? AccountId => _cloudClient.LoginStatus == CloudClientLoginStatus.LoggedIn ? _cloudClient.UserProfile?.Subject : null;

    /// <inheritdoc />
    public string? AccountLabel => AccountId is null ? null : _cloudClient.UserProfile?.Email ?? AccountId;

    /// <inheritdoc />
    public bool IsEnabled
    {
        get => AccountId is { } account && _state.EnabledChatSyncAccounts?.Contains(account) == true;
        set
        {
            if (AccountId is not { } account || value == IsEnabled)
            {
                return;
            }
            var enabled = _state.EnabledChatSyncAccounts ?? [];
            _state.EnabledChatSyncAccounts = value ? [.. enabled, account] : enabled.Where(id => id != account).ToArray();
        }
    }

    /// <inheritdoc />
    public ChatSyncSession? Session
    {
        get
        {
            lock (_gate)
            {
                return _connection?.Session;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    private readonly IDbContextFactory<ChatV2DbContext> _dbFactory;
    private readonly ChatTurnStorage _storage;
    private readonly ChatContextManager _manager;
    private readonly ICloudClient _cloudClient;
    private readonly PersistentState _state;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChatSyncService> _logger;
    private readonly Lock _gate = new();
    private readonly DebounceExecutor<ChatSyncService, ThreadingTimerImpl> _debounce;
    private (ChatSyncSession Session, HttpClient Http)? _connection;
    private Task _retirement = Task.CompletedTask;
    private bool _isDisposed;

    /// <summary>
    /// Creates the account owner without enrolling history or issuing network requests.
    /// </summary>
    public ChatSyncService(
        IDbContextFactory<ChatV2DbContext> dbFactory,
        ChatTurnStorage storage,
        ChatContextManager manager,
        ICloudClient cloudClient,
        PersistentState state,
        IHttpClientFactory httpClientFactory,
        ILogger<ChatSyncService> logger)
    {
        _dbFactory = dbFactory;
        _storage = storage;
        _manager = manager;
        _cloudClient = cloudClient;
        _state = state;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _debounce = new DebounceExecutor<ChatSyncService, ThreadingTimerImpl>(
            () => this,
            static owner => owner.Session?.RequestSync(),
            TimeSpan.FromSeconds(1));
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_state.EnabledChatSyncAccounts is null && _state.WasLegacyChatSyncEnabled)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var accounts = await db.SyncAccounts.Select(row => row.AccountId).Take(2).ToArrayAsync(cancellationToken);
            if (accounts.Length > 0)
            {
                _state.EnabledChatSyncAccounts ??= accounts.Length == 1 ? accounts : [];
            }
        }
        _cloudClient.PropertyChanged += OnAccountChanged;
        _state.PropertyChanged += OnAccountChanged;
        _manager.SyncRequested += OnSyncRequested;
        UpdateSession();
    }

    private void OnSyncRequested(object? sender, EventArgs args) => _debounce.Trigger();

    private void OnAccountChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ICloudClient.UserProfile) or nameof(ICloudClient.LoginStatus) or nameof(PersistentState.EnabledChatSyncAccounts))
        {
            UpdateSession();
        }
    }

    private void UpdateSession()
    {
        var account = AccountId;
        if (account is not null && _state.EnabledChatSyncAccounts is null)
        {
            _state.EnabledChatSyncAccounts = _state.WasLegacyChatSyncEnabled && _cloudClient.RestoredAccountId == account ? [account] : [];
            return;
        }

        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }
            _manager.SignedInAccountId = account;
            var target = IsEnabled ? account : null;
            if (_connection?.Session.AccountId != target)
            {
                RetireConnection();
                _storage.SyncAccountId = null;
                if (target is not null)
                {
                    var http = _httpClientFactory.CreateClient(nameof(IChatSyncService));
                    var transport = new ChatSyncTransport(http, target, new ChatSyncCompatibility(ChatSyncProtocol.Version, 1, 2));
                    var session = new ChatSyncSession(_dbFactory, _storage, _manager, transport, _logger);
                    _connection = (session, http);
                    session.Changed += OnSessionChanged;
                    session.RequestSync();
                }
            }
        }
        OnPropertyChanged(nameof(AccountId));
        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(Session));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionChanged(object? sender, EventArgs args)
    {
        if (ReferenceEquals(Session, sender))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RetireConnection()
    {
        if (_connection is not { } connection)
        {
            return;
        }
        _connection = null;
        connection.Session.Changed -= OnSessionChanged;
        var retired = RetireAsync(connection.Session, connection.Http);
        _retirement = _retirement.IsCompletedSuccessfully ? retired : Task.WhenAll(_retirement, retired);
    }

    private static async Task RetireAsync(ChatSyncSession session, HttpClient http)
    {
        await session.DisposeAsync().ConfigureAwait(false);
        http.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task retired;
        lock (_gate)
        {
            _isDisposed = true;
            _cloudClient.PropertyChanged -= OnAccountChanged;
            _state.PropertyChanged -= OnAccountChanged;
            _manager.SyncRequested -= OnSyncRequested;
            _debounce.Dispose();
            RetireConnection();
            _storage.SyncAccountId = null;
            retired = _retirement;
        }
        await retired.ConfigureAwait(false);
    }
}