using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Messages;
using Everywhere.Views.Pages;

namespace Everywhere.Views;

public sealed partial class UserProfileCard : TemplatedControl
{
    /// <summary>
    /// Account state shared by all profile cards.
    /// </summary>
    public static ICloudClient CloudClient { get; } = ServiceLocator.Resolve<ICloudClient>();

    /// <summary>
    /// The account menu uses the same account-bound sync preference as the management page.
    /// </summary>
    public static IChatSyncService ChatSync { get; } = ServiceLocator.Resolve<IChatSyncService>();

    /// <summary>
    /// Whether this build can offer chat synchronization. This does not change the user's preference.
    /// </summary>
    public static bool IsChatSyncConfigured => !CloudConstants.CloudBaseUrl.IsNullOrEmpty();

    public static readonly StyledProperty<bool> ShowActionsButtonProperty =
        AvaloniaProperty.Register<UserProfileCard, bool>(nameof(ShowActionsButton), true);

    public bool ShowActionsButton
    {
        get => GetValue(ShowActionsButtonProperty);
        set => SetValue(ShowActionsButtonProperty, value);
    }

    /// <summary>
    /// make this static so that busy state can be shared across all instances of UserProfileCard,
    /// and avoid multiple login/logout operations at the same time.
    /// </summary>
    public static AsyncRelayCommand LoginCommand { get; } = new(LoginAsync, () => CloudClient.LoginStatus != CloudClientLoginStatus.NotConfigured);

    private static Task LoginAsync(CancellationToken cancellationToken) =>
        CloudClient.LoginAsync(cancellationToken);

    public static AsyncRelayCommand LogoutCommand { get; } = new(LogoutAsync);

    private static Task LogoutAsync(CancellationToken cancellationToken) =>
        CloudClient.LogoutAsync(cancellationToken);

    /// <summary>
    /// Refreshes on an actual account flyout opening, not when its logged-in template is mounted.
    /// </summary>
    public static AsyncRelayCommand RefreshUserProfileCommand { get; } = new(RefreshUserProfileAsync);

    private static async Task RefreshUserProfileAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CloudClient.ReloadUserDataAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the flyout can cancel admission before the account client starts its read.
        }
    }

    [RelayCommand]
    private static void ManageChatSync() =>
        WeakReferenceMessenger.Default.Send<ApplicationMessage>(new ShowWindowMessage(ShowWindowMessage.MainWindow, typeof(ChatSyncPage)));

    [RelayCommand]
    private Task<bool> OpenDashboardAsync() => OpenUrlAsync(CloudConstants.DashboardUrl);

    [RelayCommand]
    private Task<bool> ManageSubscriptionAsync() => OpenUrlAsync(CloudConstants.ManageSubscriptionUrl);

    [RelayCommand]
    private Task<bool> GetSupportAsync() => OpenUrlAsync(CloudConstants.GetSupportUrl);

    private Task<bool> OpenUrlAsync(string url)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            return launcher.LaunchUriAsync(new Uri(url));
        }

        return Task.FromResult(false);
    }
}