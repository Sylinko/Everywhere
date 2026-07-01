using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Database;

namespace Everywhere.Views;

public sealed partial class UserProfileCard : TemplatedControl
{
    public static ICloudClient CloudClient { get; } = ServiceLocator.Resolve<ICloudClient>();

    public static readonly StyledProperty<bool> ShowActionsButtonProperty =
        AvaloniaProperty.Register<UserProfileCard, bool>(nameof(ShowActionsButton), true);

    public bool ShowActionsButton
    {
        get => GetValue(ShowActionsButtonProperty);
        set => SetValue(ShowActionsButtonProperty, value);
    }

    public PersistentState PersistentState { get; } = ServiceLocator.Resolve<PersistentState>();

    public IChatDbSynchronizer CloudSynchronizer { get; } = ServiceLocator.Resolve<IChatDbSynchronizer>();

    /// <summary>
    /// make this static so that busy state can be shared across all instances of UserProfileCard,
    /// and avoid multiple login/logout operations at the same time.
    /// </summary>
    public static AsyncRelayCommand LoginCommand { get; } = new(LoginAsync);

    private static Task LoginAsync(CancellationToken cancellationToken) =>
        CloudClient.LoginAsync(cancellationToken);

    public static AsyncRelayCommand LogoutCommand { get; } = new(LogoutAsync);

    private static Task LogoutAsync(CancellationToken cancellationToken) =>
        CloudClient.LogoutAsync(cancellationToken);

    public static AsyncRelayCommand RefreshUserProfileCommand { get; } = new(RefreshUserProfileAsync);

    private static async Task RefreshUserProfileAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CloudClient.ReloadUserDataAsync(cancellationToken);
        }
        catch
        {
            // Ignore
        }
    }

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