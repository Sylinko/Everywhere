using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Everywhere.Configuration;
using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Hosting.Loading;
using Everywhere.Modding.Hosting.Services;
using Everywhere.Modding.Packaging;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.Modding;

/// <summary>
/// Uses the real mod engine to check settings migration, UI retirement and collectible ownership.
/// </summary>
public sealed class ModSettingsTests
{
    /// <summary>
    /// Retained closed views and handles must not retain a settings activation or prevent replacement.
    /// </summary>
    [AvaloniaTest]
    public async Task Settings_HotUnloadAndReload_SavesEditsAndCollectsWithRetainedViews()
    {
        await using var harness = new SettingsHarness();
        var first = harness.Definition("settings.first");
        var second = harness.Definition("settings.second");
        File.WriteAllText(Path.Combine(first.DataDirectory, "settings.json"), """{"Version":"1.0.0","Count":3,"Limits":{"Search":9},"HostKeys":{"settings.first:1":4}}""");
        AssertApplied(await harness.Engine.ApplyAsync([first, second]));
        Assert.That(harness.Trace.Take(2), Is.EqualTo(new[] { "migrate", "migration-disposed" }));
        Assert.That(harness.Trace.Any(value => value.StartsWith("view:")), Is.False);

        var view = (ItemsControl)(harness.Registry.CreateView(first.Id) ?? throw new AssertionException("Missing settings page."));
        Assert.That(harness.Registry.CreateView(first.Id), Is.SameAs(view));
        (view.ItemsSource ?? throw new AssertionException("Missing settings items.")).OfType<SettingsBooleanItem>().Single().Value = true;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        AssertApplied(await harness.Engine.ApplyAsync([]));
        Assert.That(view.ItemsSource, Is.Null);
        Assert.That(harness.Registry.CreateView(first.Id), Is.Null);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(first.DataDirectory, "settings.json"))) ?? throw new AssertionException("Missing document.");
        Assert.Multiple(() =>
        {
            Assert.That(saved["Version"]?.GetValue<string>(), Is.EqualTo("2.0.0"));
            Assert.That(saved["Count"]?.GetValue<int>(), Is.EqualTo(12));
            Assert.That(saved["IsEnabled"]?.GetValue<bool>(), Is.True);
            Assert.That(saved["Limits"]?["Search"]?.GetValue<int>(), Is.EqualTo(9));
            Assert.That(saved["HostKeys"]?["settings.first:1"]?.GetValue<int>(), Is.EqualTo(4));
            Assert.That(JsonNode.Parse(File.ReadAllText(Path.Combine(second.DataDirectory, "settings.json")))?["Count"]?.GetValue<int>(), Is.EqualTo(2));
        });

        AssertApplied(await harness.Engine.ApplyAsync([first]));
        var replacement = harness.Registry.CreateView(first.Id);
        Assert.That(replacement, Is.Not.SameAs(view));
        await harness.Handles[0].DisposeAsync();
        Assert.That(harness.Registry.CreateView(first.Id), Is.SameAs(replacement));
        AssertApplied(await harness.Engine.ApplyAsync([]));
        Assert.That(harness.Trace.Count(value => value == "migrate"), Is.EqualTo(1));
        GC.KeepAlive(view);
        GC.KeepAlive(replacement);
        GC.KeepAlive(harness.Handles);
    }

    /// <summary>
    /// A later activation failure keeps the committed settings version and releases initialization resources.
    /// </summary>
    [AvaloniaTest]
    public async Task Settings_InitializationFailsAfterMigration_RetrySkipsCompletedMigration()
    {
        await using var harness = new SettingsHarness();
        var definition = harness.Definition("settings.retry");
        var file = Path.Combine(definition.DataDirectory, "settings.json");
        await File.WriteAllTextAsync(file, """{"Version":"1.0.0"}""");
        var marker = Path.Combine(definition.DataDirectory, "fail-initialize");
        await File.WriteAllTextAsync(marker, string.Empty);
        var failed = await harness.Engine.ApplyAsync([definition]);
        Assert.That(failed.Status, Is.EqualTo(ModApplyStatus.CompletedWithErrors), Describe(failed));
        Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(file))?["Version"]?.GetValue<string>(), Is.EqualTo("2.0.0"));
        File.Delete(marker);
        AssertApplied(await harness.Engine.ApplyAsync([definition]));
        Assert.That(harness.Trace.Count(value => value == "migrate"), Is.EqualTo(1));
        AssertApplied(await harness.Engine.ApplyAsync([]));
    }

    /// <summary>
    /// Invalid model data must not be replaced by defaults or retained through the failed initialization task.
    /// </summary>
    [AvaloniaTest]
    public async Task Settings_InvalidModelData_PreservesFileAndCollectsFailedActivation()
    {
        await using var harness = new SettingsHarness();
        var definition = harness.Definition("settings.invalid");
        var file = Path.Combine(definition.DataDirectory, "settings.json");
        const string original = """{"Version":"2.0.0","Count":"invalid"}""";
        await File.WriteAllTextAsync(file, original);

        var failed = await harness.Engine.ApplyAsync([definition]);
        Assert.That(failed.Status, Is.EqualTo(ModApplyStatus.CompletedWithErrors), Describe(failed));
        Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(original));
        Assert.That(harness.Registry.CreateView(definition.Id), Is.Null);
        Assert.That(harness.Trace, Does.Not.Contain("opened:" + definition.Id));
    }

    /// <summary>
    /// Ordinary exit saves while the UI thread is deliberately unable to service dispatcher work.
    /// </summary>
    [AvaloniaTest]
    public async Task Settings_ProcessExitWithOpenView_FlushesWithoutDispatcher()
    {
        await using var harness = new SettingsHarness();
        var definition = harness.Definition("settings.exit");
        AssertApplied(await harness.Engine.ApplyAsync([definition]));
        var view = harness.Registry.CreateView(definition.Id);
        var exit = Task.Run(async () => await harness.Engine.DisposeAsync());
        Assert.That(exit.Wait(TimeSpan.FromSeconds(10)), Is.True, "Exit waited for the blocked UI dispatcher.");
        await exit;
        Assert.That(harness.Trace, Does.Contain("disposed:Exit"));
        Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(definition.DataDirectory, "settings.json")))?["Count"]?.GetValue<int>(), Is.EqualTo(2));
        Assert.That(harness.Registry.CreateView(definition.Id), Is.Null);
        GC.KeepAlive(view);
    }

    /// <summary>
    /// Production templates edit a collectible model, then release its editors while the host window remains open.
    /// </summary>
    [AvaloniaTest]
    public async Task Settings_MountedEditorOnHotUnload_DetachesSavesAndCollects()
    {
        await using var harness = new SettingsHarness();
        var definition = harness.Definition("settings.editor");
        AssertApplied(await harness.Engine.ApplyAsync([definition]));
        var window = await MountAndEditAsync(harness.Registry, definition.Id);

        try
        {
            AssertApplied(await harness.Engine.ApplyAsync([]));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var view = window.Content as ItemsControl ?? throw new AssertionException("Missing retained settings view.");
            Assert.That(view.ItemsSource, Is.Null);
            Assert.That(view.GetVisualDescendants().OfType<ToggleSwitch>(), Is.Empty);
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(definition.DataDirectory, "settings.json")));
            Assert.That(saved?["IsEnabled"]?.GetValue<bool>(), Is.True);
            GC.KeepAlive(window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Keeps editor locals out of the engine's collection-check stack without closing the host window.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<SettingsEditorTestWindow> MountAndEditAsync(ModSettingsRegistry registry, string modId)
    {
        var view = registry.CreateView(modId) as SettingsItemsControl ?? throw new AssertionException("Missing settings view.");
        var window = new SettingsEditorTestWindow(view);
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            var editor = view.GetVisualDescendants().OfType<ToggleSwitch>().Single();
            editor.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            return window;
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    /// <summary>
    /// Requires confirmed collection, rather than treating restart as a successful hot unload.
    /// </summary>
    private static void AssertApplied(ModApplyResult result)
    {
        Assert.That(result.Status, Is.EqualTo(ModApplyStatus.Applied), Describe(result));
    }

    /// <summary>
    /// Keeps diagnostics printable without exposing collectible exception objects.
    /// </summary>
    private static string Describe(ModApplyResult result) => string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Detail));

    /// <summary>
    /// Builds an isolated declared package whose fixture comes from a separate collectible copy of this assembly.
    /// </summary>
    private sealed class SettingsHarness : IAsyncDisposable
    {
        /// <summary>
        /// Gets the runtime under test.
        /// </summary>
        public ModEngine Engine { get; }

        /// <summary>
        /// Gets the application settings UI registry.
        /// </summary>
        public ModSettingsRegistry Registry { get; } = new();

        /// <summary>
        /// Gets host-owned execution events.
        /// </summary>
        public IList<string> Trace { get; } = new List<string>();

        /// <summary>
        /// Retains closed handles to expose accidental ownership after retirement.
        /// </summary>
        public List<IAsyncDisposable> Handles { get; } = [];

        private readonly string _root = Path.Combine(Path.GetTempPath(), "Everywhere.Settings.Tests", Guid.NewGuid().ToString("N"));
        private readonly HostServiceContainer _services;

        /// <summary>
        /// Shares declared test-host assemblies while excluding the collectible fixture assembly itself.
        /// </summary>
        public SettingsHarness()
        {
            Directory.CreateDirectory(_root);
            _services = new HostServiceContainer(new ServiceCollection()
                .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
                .AddSingleton<IModSettingsRegistry>(Registry)
                .AddSingleton(Trace)
                .AddSingleton<ICollection<IAsyncDisposable>>(Handles)
                );
            var fixture = typeof(ModSettingsTests).Assembly.Location;
            var hostFiles = Directory.GetFiles(AppContext.BaseDirectory, "*.dll").Where(path => path != fixture && IsManaged(path));
            Engine = new ModEngine(new HostAssemblyCatalog(hostFiles), _services, new ModEngineOptions());
        }

        /// <summary>
        /// Creates a package with a distinct identity and data directory but the same shared fixture bytes.
        /// </summary>
        public ModDefinition Definition(string id)
        {
            var package = Path.Combine(_root, id);
            Directory.CreateDirectory(package);
            var assembly = typeof(ModSettingsTests).Assembly.Location;
            var name = Path.GetFileName(assembly);
            File.Copy(assembly, Path.Combine(package, name));
            var manifest = new ModManifest
            {
                Id = id,
                Version = "1.0.0",
                Entry = new UniversalModEntry
                {
                    Assembly = name,
                    Type = "Everywhere.Core.Tests.Modding.Fixtures.CollectibleSettingsMod",
                    Assemblies = [name],
                    Framework = "net10.0",
                    ModApiVersion = "[1.0.0,2.0.0)"
                }
            };
            ModManifestFile.Write(package, manifest);
            var data = Path.Combine(_root, "data", id);
            Directory.CreateDirectory(data);
            return new ModDefinition(new ModInstallation { InstallationId = id, Directory = package, Manifest = manifest },
                data, "win-x64", "1.0.0", "1.0.0", "net10.0");
        }

        /// <summary>
        /// Excludes native payloads from the test host's declared managed catalog.
        /// </summary>
        private static bool IsManaged(string path)
        {
            try
            {
                AssemblyName.GetAssemblyName(path);
                return true;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await _services.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }
}
