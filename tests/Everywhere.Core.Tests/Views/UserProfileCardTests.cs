using System.Reflection;
using Avalonia.Headless.NUnit;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Database;
using Everywhere.Interop;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Everywhere.Core.Tests.Views;

[NonParallelizable]
public sealed class UserProfileCardTests
{
    [AvaloniaTest]
    [TestCase("OpenDashboardCommand", "/dashboard", "")]
    [TestCase("ManageSubscriptionCommand", "/everywhere", "")]
    [TestCase("GetSupportCommand", "/everywhere", "?tab=support")]
    public async Task AccountAction_WithoutWindow_UsesApplicationLauncher(string commandName, string path, string query)
    {
        var locator = typeof(ServiceLocator).GetField("_serviceProvider", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(ServiceLocator));
        var previousProvider = locator.GetValue(null);
        var previousLauncher = BetterBclLauncher.Shared;
        using var provider = new ServiceCollection()
            .AddSingleton(Substitute.For<ICloudClient>())
            .AddSingleton(new PersistentState(new InMemoryKeyValueStorage()))
            .AddSingleton(Substitute.For<IChatDbSynchronizer>())
            .BuildServiceProvider();
        var launcher = Substitute.For<ILauncher>();
        launcher.LaunchUriAsync(Arg.Any<Uri>()).Returns(Task.FromResult(true));
        try
        {
            locator.SetValue(null, provider);
            BetterBclLauncher.Shared = launcher;
            // A detached card has no window launcher. Account actions must use
            // the configured application route, including when hosted in a flyout.
            var card = new UserProfileCard();
            var command = typeof(UserProfileCard).GetProperty(commandName)?.GetValue(card) as IAsyncRelayCommand
                ?? throw new MissingMemberException(commandName);
            await command.ExecuteAsync(null);
            await launcher.Received(1).LaunchUriAsync(Arg.Is<Uri>(uri => uri.AbsolutePath == path && uri.Query == query));
        }
        finally
        {
            BetterBclLauncher.Shared = previousLauncher;
            locator.SetValue(null, previousProvider);
        }
    }
}