using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Plugin.Hosting;
using Everywhere.Plugin.Hosting.Installation;
using Everywhere.Plugin.Hosting.Loading;
using Everywhere.Plugin.Packaging;
using Everywhere.ViewModels;
using Everywhere.Views;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Core.Tests.Plugins;

/// <summary>
/// Exercises navigation ownership and the separately packaged UI sample without statically referencing its types.
/// </summary>
public sealed class PluginNavigationTests
{
    /// <summary>
    /// Removing a selected contribution preserves the remaining navigation objects and selects a surviving page.
    /// </summary>
    [AvaloniaTest]
    public async Task RegisterNavigationItem_RemoveSelectedContribution_PreservesOtherItems()
    {
        using var services = CreateServices();
        var navigation = services.GetRequiredService<MainViewModel>();
        var first = navigation.RegisterNavigationItem(new TestPage());
        var second = navigation.RegisterNavigationItem(new TestPage());
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var survivor = navigation.Items[0];
        navigation.SelectedItem = navigation.Items[1];

        await second.DisposeAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.That(navigation.Items.Count, Is.EqualTo(1));
        Assert.That(navigation.SelectedItem, Is.SameAs(survivor));
        await first.DisposeAsync();
        Assert.That(navigation.SelectedItem, Is.Null);
    }

    /// <summary>
    /// Runs the real SDK output through collectible loading, XAML rendering, language changes and registered cleanup.
    /// Collection is reported separately because UI framework caches may retain plugin types after detachment.
    /// </summary>
    [AvaloniaTest]
    [Explicit("Build the Windows host and UI sample first; set EVERYWHERE_UI_PLUGIN_HOST and EVERYWHERE_UI_PLUGIN_PACKAGE.")]
    public async Task PackagedSample_ShowAndRemove_UpdatesTranslationsAndReportsCollection()
    {
        var hostDirectory = Environment.GetEnvironmentVariable("EVERYWHERE_UI_PLUGIN_HOST")
            ?? throw new AssertionException("Set EVERYWHERE_UI_PLUGIN_HOST to the built host directory.");
        var package = Environment.GetEnvironmentVariable("EVERYWHERE_UI_PLUGIN_PACKAGE")
            ?? throw new AssertionException("Set EVERYWHERE_UI_PLUGIN_PACKAGE to the sample ZIP.");
        var root = Path.Combine(Path.GetTempPath(), "Everywhere.Plugin.Ui.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalLocale = LocaleManager.CurrentLocale;

        try
        {
            var sources = Path.Combine(root, "profile", "plugins");
            Directory.CreateDirectory(sources);
            File.Copy(package, Path.Combine(sources, Path.GetFileName(package)));
            var store = new PluginPackageStore(
                Path.Combine(root, "app-data", "plugin-installations"),
                Path.Combine(root, "profile", "plugin-data"),
                Path.Combine(root, "app-data", "cache", "plugin-staging"));
            var installations = await new PluginSourceCatalog(sources, store).ImportAsync();
            var installation = installations.Single();
            var host = new HostReferenceSet(hostDirectory);
            using var services = CreateServices();
            var navigation = services.GetRequiredService<MainViewModel>();
            // This test process is the host. Use its built copies of declared shared dependencies;
            // platform application builds may have different generated or patched module identities.
            var hostPaths = host.Assemblies.Values.Select(path =>
            {
                var local = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(path));
                return File.Exists(local) ? local : path;
            });
            var engine = new PluginEngine(
                new HostAssemblyCatalog(hostPaths),
                services,
                new PluginEngineOptions
                {
                    CollectionAttempts = 30,
                    CollectionDelay = TimeSpan.FromMilliseconds(50)
                });
            var definition = new PluginDefinition(
                installation,
                store.GetDataDirectory(installation),
                host.Descriptor.RuntimeIdentifier,
                host.Descriptor.HostVersion,
                host.Descriptor.PluginApiVersion,
                host.Descriptor.Framework);

            var loaded = await engine.ApplyAsync([definition]);
            Assert.That(loaded.Status, Is.EqualTo(PluginApplyStatus.Applied), Describe(loaded));
            await ShowSampleAsync(navigation);
            var stopped = await engine.ApplyAsync([]);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            Assert.That(navigation.Items.Count, Is.Zero);
            Assert.That(navigation.SelectedItem, Is.Null);
            Assert.That(LocaleManager.TryGetString("SamplePlugin_Title", out _), Is.False);
            Assert.That(stopped.ActivePluginIds, Is.Empty);
            Assert.That(stopped.Status, Is.AnyOf(PluginApplyStatus.Applied, PluginApplyStatus.RestartRequired), Describe(stopped));
            Assert.That(stopped.Diagnostics.All(diagnostic => diagnostic.Code == "CollectionUnconfirmed"), Is.True, Describe(stopped));
            TestContext.Out.WriteLine($"UI sample cleanup: {stopped.Status}. {Describe(stopped)}");
        }
        finally
        {
            LocaleManager.CurrentLocale = originalLocale;
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Keeps plugin controls outside the caller's collection-check stack and async state machine.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ShowSampleAsync(MainViewModel navigation)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var item = navigation.Items.Single();
        navigation.SelectedItem = item;
        var window = new Window { Width = 900, Height = 600, Content = item.Route };
        window.Show();

        try
        {
            LocaleManager.CurrentLocale = LocaleName.En;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.That(item.Content, Is.EqualTo("Plugin sample"));
            Assert.That(window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Plugin sample"), Is.True);

            LocaleManager.CurrentLocale = LocaleName.ZhHans;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.That(item.Content, Is.EqualTo("插件示例"));
            Assert.That(window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "插件示例"), Is.True);
            window.GetVisualDescendants().OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsVisible && text.Text == "插件的按钮可以正常工作！"), Is.True);
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    /// <summary>
    /// Uses the production view model with in-memory configuration and no unrelated startup services.
    /// </summary>
    private static ServiceProvider CreateServices()
    {
        return new ServiceCollection()
            .AddSingleton<Settings>()
            .AddSingleton<IKeyValueStorage, InMemoryKeyValueStorage>()
            .AddSingleton<PersistentState>()
            .AddSingleton<MainViewModel>()
            .BuildServiceProvider();
    }

    /// <summary>
    /// Formats host-owned values for assertion and diagnostic output.
    /// </summary>
    private static string Describe(PluginApplyResult result) => string.Join("; ", result.Diagnostics.Select(item => item.Code + ": " + item.Detail));

    /// <summary>
    /// Provides a host-owned contribution for navigation-only checks.
    /// </summary>
    private sealed class TestPage : IMainViewNavigationTopLevelItem
    {
        /// <inheritdoc />
        public int Index => 1;

        /// <inheritdoc />
        public LucideIconKind Icon => LucideIconKind.Box;

        /// <inheritdoc />
        public IDynamicLocaleKey TitleKey { get; } = new DirectLocaleKey("Test page");
    }
}
