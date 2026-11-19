using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Hosting.Loading;
using Everywhere.Modding.Hosting.Services;
using Everywhere.Modding.Packaging;
using Everywhere.ViewModels;
using Everywhere.Views;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Core.Tests.Modding;

/// <summary>
/// Exercises navigation ownership and the separately packaged UI sample without statically referencing its types.
/// </summary>
public sealed class ModNavigationTests
{
    /// <summary>
    /// Removing a selected contribution preserves the remaining navigation objects and selects a surviving page.
    /// </summary>
    [AvaloniaTest]
    public async Task RegisterNavigationItem_RemoveSelectedContribution_PreservesOtherItems()
    {
        await using var services = CreateServices();
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
    /// Collection is reported separately because UI framework caches may retain mod types after detachment.
    /// </summary>
    [AvaloniaTest]
    [Explicit("Build the Windows host and UI sample first; set EVERYWHERE_UI_MOD_HOST and EVERYWHERE_UI_MOD_PACKAGE.")]
    public async Task PackagedSample_ShowAndRemove_UpdatesTranslationsAndReportsCollection()
    {
        var hostDirectory = Environment.GetEnvironmentVariable("EVERYWHERE_UI_MOD_HOST")
            ?? throw new AssertionException("Set EVERYWHERE_UI_MOD_HOST to the built host directory.");
        var package = Environment.GetEnvironmentVariable("EVERYWHERE_UI_MOD_PACKAGE")
            ?? throw new AssertionException("Set EVERYWHERE_UI_MOD_PACKAGE to the sample ZIP.");
        var root = Path.Combine(Path.GetTempPath(), "Everywhere.Modding.Ui.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalLocale = LocaleManager.CurrentLocale;

        try
        {
            var sources = Path.Combine(root, "profile", "mods");
            Directory.CreateDirectory(sources);
            File.Copy(package, Path.Combine(sources, Path.GetFileName(package)));
            var store = new ModPackageStore(
                Path.Combine(root, "app-data", "cache", "mods"),
                Path.Combine(root, "profile", "mods-data"),
                Path.Combine(root, "app-data", "cache", "mods", "staging"));
            var imported = await new ModSourceCatalog(sources, store).ImportAsync();
            var installation = imported.Installations.Single();
            var host = new HostReferenceSet(hostDirectory);
            await using var services = CreateServices();
            var navigation = services.GetRequiredService<MainViewModel>();
            // This test process is the host. Use its built copies of declared shared dependencies;
            // platform application builds may have different generated or patched module identities.
            var hostPaths = host.Assemblies.Values.Select(path =>
            {
                var local = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(path));
                return File.Exists(local) ? local : path;
            });
            var engine = new ModEngine(
                new HostAssemblyCatalog(hostPaths),
                services,
                new ModEngineOptions
                {
                    CollectionAttempts = 30,
                    CollectionDelay = TimeSpan.FromMilliseconds(50)
                });
            var definition = new ModDefinition(
                installation,
                store.GetDataDirectory(installation),
                host.Descriptor.RuntimeIdentifier,
                host.Descriptor.HostVersion,
                host.Descriptor.ModApiVersion,
                host.Descriptor.Framework);

            var loaded = await engine.ApplyAsync([definition]);
            Assert.That(loaded.Status, Is.EqualTo(ModApplyStatus.Applied), Describe(loaded));
            await ShowSampleAsync(navigation);
            var stopped = await engine.ApplyAsync([]);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            Assert.That(navigation.Items.Count, Is.Zero);
            Assert.That(navigation.SelectedItem, Is.Null);
            Assert.That(LocaleManager.TryGetString("SampleMod_Title", out _), Is.False);
            Assert.That(stopped.ActiveModIds, Is.Empty);
            Assert.That(stopped.Status, Is.AnyOf(ModApplyStatus.Applied, ModApplyStatus.RestartRequired), Describe(stopped));
            Assert.That(stopped.Diagnostics.All(diagnostic => diagnostic.Code == "CollectionUnconfirmed"), Is.True, Describe(stopped));
            await TestContext.Out.WriteLineAsync($"UI sample cleanup: {stopped.Status}. {Describe(stopped)}");
        }
        finally
        {
            LocaleManager.CurrentLocale = originalLocale;
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Keeps mod controls outside the caller's collection-check stack and async state machine.
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
            Assert.That(item.Content, Is.EqualTo("Mod sample"));
            Assert.That(window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Mod sample"), Is.True);

            LocaleManager.CurrentLocale = LocaleName.ZhHans;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.That(item.Content, Is.EqualTo("模组示例"));
            Assert.That(window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "模组示例"), Is.True);
            window.GetVisualDescendants().OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(window.GetVisualDescendants().OfType<TextBlock>().Any(text => text is { IsVisible: true, Text: "模组的按钮可以正常工作！" }), Is.True);
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
    private static HostServiceContainer CreateServices()
    {
        return new HostServiceContainer(
            new ServiceCollection()
                .AddSingleton<Settings>()
                .AddSingleton<IKeyValueStorage, InMemoryKeyValueStorage>()
                .AddSingleton<PersistentState>()
                .AddSingleton<MainViewModel>()
        );
    }

    /// <summary>
    /// Formats host-owned values for assertion and diagnostic output.
    /// </summary>
    private static string Describe(ModApplyResult result) => string.Join("; ", result.Diagnostics.Select(item => item.Code + ": " + item.Detail));

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
