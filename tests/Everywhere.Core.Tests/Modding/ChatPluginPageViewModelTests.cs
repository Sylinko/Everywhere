using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DynamicData;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.ViewModels;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Everywhere.Core.Tests.Modding;

/// <summary>
/// Checks chat settings presentation independently of any packaged mod or native runtime.
/// </summary>
public sealed class ChatPluginPageViewModelTests
{
    /// <summary>
    /// Switching tools clears the retained tab and removes its real editor without changing the next tool's settings.
    /// </summary>
    [AvaloniaTest]
    public async Task SelectedPlugin_ChangesSelection_DetachesRetainedSettingsEditor()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(services);
        using var manager = CreateManager(services, settings);
        using var first = new SettingsChatPlugin("first");
        using var second = new SettingsChatPlugin("second");
        await using var firstRegistration = manager.RegisterChatPlugin(first);
        await using var secondRegistration = manager.RegisterChatPlugin(second);
        await DrainUiAsync();
        using var page = new ChatPluginPageViewModel(manager, settings);
        page.SelectedPlugin = page.ToolSettings.Plugins.Single(item => ReferenceEquals(item.Plugin, first));
        var tab = page.ContentTabItems.OfType<ChatPluginPageViewModel.SettingsTabItem>().Single();
        var view = CreateSettingsView(tab);
        var window = new SettingsEditorTestWindow(view);
        window.Show();

        try
        {
            await DrainUiAsync();
            window.UpdateLayout();
            var editor = view.GetVisualDescendants().OfType<ToggleSwitch>().Single();
            editor.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Assert.That(first.BooleanItem.Value, Is.True);
            page.SelectedPlugin = page.ToolSettings.Plugins.Single(item => ReferenceEquals(item.Plugin, second));
            await DrainUiAsync();
            Assert.Multiple(() =>
            {
                Assert.That(tab.SettingsItems, Is.Null);
                Assert.That(view.ItemsSource, Is.Null);
                Assert.That(view.GetVisualDescendants().OfType<ToggleSwitch>(), Is.Empty);
                Assert.That(page.ContentTabItems.OfType<ChatPluginPageViewModel.SettingsTabItem>().Single().SettingsItems,
                    Is.SameAs(second.SettingsItems));
                Assert.That(second.BooleanItem.Value, Is.False);
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Worker-thread removal clears selected details after UI delivery; removing another tool preserves selection.
    /// </summary>
    [AvaloniaTest]
    public async Task RegisterChatPlugin_RemovedFromWorker_ClearsSelectedSettingsAfterUiDrain()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(services);
        using var manager = CreateManager(services, settings);
        using var first = new SettingsChatPlugin("first");
        using var second = new SettingsChatPlugin("second");
        await using var firstRegistration = manager.RegisterChatPlugin(first);
        await using var secondRegistration = manager.RegisterChatPlugin(second);
        await DrainUiAsync();
        using var page = new ChatPluginPageViewModel(manager, settings);
        var selected = page.ToolSettings.Plugins.Single(item => ReferenceEquals(item.Plugin, first));
        page.SelectedPlugin = selected;
        var tab = page.ContentTabItems.OfType<ChatPluginPageViewModel.SettingsTabItem>().Single();
        var view = CreateSettingsView(tab);
        await Task.Run(async () => await secondRegistration.DisposeAsync());
        await DrainUiAsync();
        Assert.That(page.SelectedPlugin, Is.SameAs(selected));
        await Task.Run(async () => await firstRegistration.DisposeAsync());
        await DrainUiAsync();
        Assert.Multiple(() =>
        {
            Assert.That(page.SelectedPlugin, Is.Null);
            Assert.That(page.ContentTabItems, Is.Empty);
            Assert.That(tab.SettingsItems, Is.Null);
            Assert.That(view.ItemsSource, Is.Null);
        });
    }

    /// <summary>
    /// A retained catalog, old tab and bound view cannot keep the disposed page alive through its event subscription.
    /// </summary>
    [AvaloniaTest]
    public async Task Dispose_WithSelectedSettings_ClearsBindingsAndReleasesCatalogSubscription()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(services);
        using var manager = CreateManager(services, settings);
        using var plugin = new SettingsChatPlugin("selected");
        await using var registration = manager.RegisterChatPlugin(plugin);
        await DrainUiAsync();
        var retained = CreateAndDisposePage(manager, settings);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.Multiple(() =>
        {
            Assert.That(retained.Page.IsAlive, Is.False);
            Assert.That(retained.Tab.SettingsItems, Is.Null);
            Assert.That(retained.View.ItemsSource, Is.Null);
        });
        GC.KeepAlive(retained.Catalog);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Page, object Catalog, ChatPluginPageViewModel.SettingsTabItem Tab, SettingsItemsControl View)
        CreateAndDisposePage(ChatPluginManager manager, Settings settings)
    {
        var page = new ChatPluginPageViewModel(manager, settings);
        page.SelectedPlugin = page.ToolSettings.Plugins.Single();
        var tab = page.ContentTabItems.OfType<ChatPluginPageViewModel.SettingsTabItem>().Single();
        var view = CreateSettingsView(tab);
        var catalog = page.ToolSettings.Plugins;
        page.Dispose();
        return (new WeakReference(page), catalog, tab, view);
    }

    private static SettingsItemsControl CreateSettingsView(ChatPluginPageViewModel.SettingsTabItem tab)
    {
        var view = new SettingsItemsControl { DataContext = tab };
        view.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(ChatPluginPageViewModel.SettingsTabItem.SettingsItems)));
        return view;
    }

    private static async Task DrainUiAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

    private static ChatPluginManager CreateManager(IServiceProvider services, Settings settings)
    {
        return new ChatPluginManager(services, [], settings, Substitute.For<IRuntimeManager>(), NullLogger<ChatPluginManager>.Instance);
    }

    private sealed class SettingsChatPlugin : ModChatPlugin
    {
        public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Test settings");

        public override IDynamicLocaleKey DescriptionKey { get; } = new DirectLocaleKey("Settings presentation fixture");

        public SettingsBooleanItem BooleanItem { get; } = new() { Value = false };

        public override IReadOnlyList<SettingsItem> SettingsItems { get; }

        public SettingsChatPlugin(string name) : base(name)
        {
            SettingsItems = new SettingsItems { BooleanItem };
            _functionsSource.Add(new NativeChatFunction(Echo, ChatFunctionPermissions.None));
        }

        private static string Echo(string text) => text;
    }
}