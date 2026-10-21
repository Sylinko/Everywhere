using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Hosting.Loading;
using Everywhere.Modding.Hosting.Services;
using Everywhere.Modding.Packaging;
using Everywhere.ProcessIsolation.Watchdog;
using Everywhere.ViewModels;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using NSubstitute;

namespace Everywhere.Core.Tests.Modding;

/// <summary>
/// Exercises the actual packaged Everything mod without referencing its collectible implementation.
/// </summary>
public sealed class EverythingModTests
{
    /// <summary>
    /// Uses isolated Everything 1.5 instances as user-owned processes, including settings edits and two activations.
    /// Old tool functions and settings views remain reachable while the mod and its native DLLs must unload.
    /// </summary>
    [AvaloniaTest]
    [Explicit("Build the Windows host and Everything ZIP; set EVERYWHERE_EVERYTHING_HOST and EVERYWHERE_EVERYTHING_PACKAGE.")]
    [Platform("Win")]
    public async Task PackagedEverything_QueryAndUnload_PreservesUserProcessAndRevokesTools()
    {
        var hostDirectory = Environment.GetEnvironmentVariable("EVERYWHERE_EVERYTHING_HOST")
            ?? throw new AssertionException("Set EVERYWHERE_EVERYTHING_HOST to the host build directory.");
        var package = Environment.GetEnvironmentVariable("EVERYWHERE_EVERYTHING_PACKAGE")
            ?? throw new AssertionException("Set EVERYWHERE_EVERYTHING_PACKAGE to the mod ZIP.");
        var root = Path.Combine(Path.GetTempPath(), "Everywhere.Everything.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new ModPackageStore(Path.Combine(root, "installed"), Path.Combine(root, "data"), Path.Combine(root, "staging"));
        await using var archive = File.OpenRead(package);
        var installation = await store.ImportAsync(archive);
        Assert.Multiple(() =>
        {
            Assert.That(installation.Manifest.Name["EN"], Is.EqualTo("Everything"));
            Assert.That(installation.Manifest.Description["zh-Hans"], Is.Not.Empty);
            Assert.That(installation.Manifest.Readme.Keys, Is.EquivalentTo(new[] { "en", "zh-hans" }));
            Assert.That(File.Exists(PackagePath.Resolve(installation.Directory, installation.Manifest.Readme["zh-Hans"])), Is.True);
        });
        var host = new HostReferenceSet(hostDirectory);
        var definition = new ModDefinition(
            installation,
            store.GetDataDirectory(installation),
            "win-x64",
            host.Descriptor.HostVersion,
            host.Descriptor.ModApiVersion,
            host.Descriptor.Framework);
        var executable = PackagePath.Resolve(
            installation.Directory,
            definition.Entry.ContentFiles.Single(path => Path.GetFileName(path) == "Everything.exe"));
        var instanceName = "Everywhere-Integration-" + Guid.NewGuid().ToString("N");
        var userData = Path.Combine(root, "user-instance");
        Directory.CreateDirectory(userData);
        var files = Path.Combine(root, "files");
        Directory.CreateDirectory(files);
        await File.WriteAllTextAsync(Path.Combine(files, "everywhere-integration-file.txt"), "sample");
        await File.WriteAllTextAsync(
            Path.Combine(userData, "Everything.ini"),
            "[Everything]\r\napp_data=0\r\nrun_as_admin=0\r\nindex_as_admin=0\r\nshow_tray_icon=0\r\n");
        using var userProcess = Start(
            executable,
            userData,
            "-instance",
            instanceName,
            "-startup",
            "-config",
            Path.Combine(userData, "Everything.ini"),
            "-db",
            Path.Combine(userData, "Everything.db"),
            "-no-auto-include",
            "-no-auto-index",
            "-folders",
            files,
            "-is-run-as");
        var secondInstanceName = "Everywhere-Integration-" + Guid.NewGuid().ToString("N");
        var secondUserData = Path.Combine(root, "second-user-instance");
        var secondFiles = Path.Combine(root, "second-files");
        Directory.CreateDirectory(secondUserData);
        Directory.CreateDirectory(secondFiles);
        await File.WriteAllTextAsync(Path.Combine(secondFiles, "everywhere-integration-second.txt"), "second index");
        File.Copy(Path.Combine(userData, "Everything.ini"), Path.Combine(secondUserData, "Everything.ini"));
        using var secondUserProcess = Start(
            executable,
            secondUserData,
            "-instance",
            secondInstanceName,
            "-startup",
            "-config",
            Path.Combine(secondUserData, "Everything.ini"),
            "-db",
            Path.Combine(secondUserData, "Everything.db"),
            "-no-auto-include",
            "-no-auto-index",
            "-folders",
            secondFiles,
            "-is-run-as");
        var watchdog = Substitute.For<IWatchdogManager>();
        await using var services = new HostServiceContainer(
            new ServiceCollection()
                .AddLogging()
                .AddSingleton<Settings>()
                .AddSingleton(Substitute.For<IRuntimeManager>())
                .AddSingleton(watchdog)
                .AddSingleton<IChatPluginManager, ChatPluginManager>()
                .AddSingleton(Substitute.For<IChatPluginDisplaySink>())
        );
        var manager = services.GetRequiredService<IChatPluginManager>();
        using var page = new ChatPluginPageViewModel(manager, services.GetRequiredService<Settings>());
        var hostPaths = host.Assemblies.Values.Select(path =>
        {
            var local = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(path));
            return File.Exists(local) ? local : path;
        });
        await using var engine = new ModEngine(
            new HostAssemblyCatalog(hostPaths),
            services,
            new ModEngineOptions
            {
                CollectionAttempts = 30,
                CollectionDelay = TimeSpan.FromMilliseconds(50)
            });

        try
        {
            var skipped = new ModDefinition(installation, definition.DataDirectory, "osx-arm64", "0.0.0", "0.0.0", "net10.0");
            AssertApplied(await engine.ApplyAsync([skipped]));
            Assert.That(engine.Mods.Single().State, Is.EqualTo(ModState.Skipped));
            Assert.That(engine.Mods.Single().IsEnabled, Is.True);
            Directory.CreateDirectory(definition.DataDirectory);
            var settingsPath = Path.Combine(definition.DataDirectory, "settings.json");
            await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new { Version = "1.0.0", InstanceName = instanceName }));

            for (var round = 0; round < 2; round++)
            {
                AssertApplied(await engine.ApplyAsync([definition]));
                var function = await QueryAsync(
                    manager,
                    services,
                    round == 0 ? "everywhere-integration-file.txt" : "everywhere-integration-second.txt");
                var view = EditSettings(page, executable, round == 0 ? instanceName : secondInstanceName, secondInstanceName);
                if (round == 0)
                {
                    // The already-created chat snapshot must use the changed target without re-registering tools.
                    var result = await function.InvokeAsync(
                        new Kernel(services),
                        new KernelArguments { ["searchPattern"] = "everywhere-integration-" });
                    Assert.That(result.GetValue<string>(), Does.Contain("everywhere-integration-second.txt"));
                    Assert.That(result.GetValue<string>(), Does.Not.Contain("everywhere-integration-file.txt"));
                }

                // Automatic saving must complete while the mod is still active, independently of final disposal.
                Assert.That(() => ReadSavedInstanceName(settingsPath), Is.EqualTo(secondInstanceName).After(5000, 50));
                AssertApplied(await engine.ApplyAsync([]));
                Assert.That(userProcess.HasExited, Is.False);
                Assert.That(secondUserProcess.HasExited, Is.False);
                Assert.ThrowsAsync<InvalidOperationException>(async () => await function.InvokeAsync(new Kernel(services)));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.That(manager.NativePlugins, Is.Empty);
                Assert.That(page.SelectedPlugin, Is.Null);
                Assert.That(page.ContentTabItems, Is.Empty);
                Assert.That(view.ItemsSource, Is.Null);
                var retiredTab = view.DataContext as ChatPluginPageViewModel.SettingsTabItem
                    ?? throw new AssertionException("Missing retained settings tab.");
                Assert.That(retiredTab.SettingsItems, Is.Null);
                using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
                Assert.That(saved.RootElement.GetProperty("ExecutablePath").GetString(), Is.EqualTo(executable));
                Assert.That(saved.RootElement.GetProperty("InstanceName").GetString(), Is.EqualTo(secondInstanceName));
                Assert.That(LocaleManager.TryGetString("Everything_Header", out _), Is.False);
                using var process = Process.GetCurrentProcess();
                Assert.That(
                    process.Modules.Cast<ProcessModule>().Any(module => module.FileName.StartsWith(
                        installation.Directory,
                        StringComparison.OrdinalIgnoreCase)),
                    Is.False);
            }

            Assert.That(watchdog.ReceivedCalls(), Is.Empty, "User-owned processes must never be registered with Watchdog.");
        }
        finally
        {
            await StopAsync(userProcess, executable, userData, instanceName);
            await StopAsync(secondUserProcess, executable, secondUserData, secondInstanceName);

            // Preserve evidence when collection failed and Windows still holds an installed native DLL.
            if (!engine.IsRestartRequired) Directory.Delete(root, recursive: true);
            else await TestContext.Out.WriteLineAsync("Unconfirmed installation retained at " + root);
        }
    }

    /// <summary>
    /// Completes invocation frames before the caller verifies collectible ownership.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<KernelFunction> QueryAsync(IChatPluginManager manager, IServiceProvider services, string fileName)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        using var chat = new ChatContext();
        Assert.That(manager.NativePlugins.Single().Key, Is.EqualTo("mod.everything"));
        var rules = new ObservableToolRulesets { [ToolSettingsKey.ForPlugin(manager.NativePlugins.Single())] = true };
        var scope = await manager.CreateScopeAsync(new CustomAssistant(), chat, rules, CancellationToken.None);
        var function = scope.Plugins.Single().Single().Clone();
        var result = await function.InvokeAsync(new Kernel(services), new KernelArguments { ["searchPattern"] = fileName });
        Assert.That(result.GetValue<string>(), Does.Contain(fileName));
        return function;
    }

    /// <summary>
    /// Edits generated settings through the actual chat-page entry without retaining mod-defined models or bindings.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ItemsControl EditSettings(
        ChatPluginPageViewModel page,
        string executable,
        string expectedInstance,
        string nextInstance)
    {
        page.SelectedPlugin = page.ToolSettings.NativePlugins.Single();
        var icon = page.SelectedPlugin.Plugin.BeautifulIcon ?? throw new AssertionException("Missing embedded Everything icon.");
        var svg = XDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(icon)));
        Assert.That(svg.Root?.Name, Is.EqualTo(XName.Get("svg", "http://www.w3.org/2000/svg")));
        var tab = page.ContentTabItems.OfType<ChatPluginPageViewModel.SettingsTabItem>().Single();
        Assert.That(tab.SettingsItems, Is.SameAs(page.SelectedPlugin.Plugin.SettingsItems));
        var view = new SettingsItemsControl { DataContext = tab };
        view.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(ChatPluginPageViewModel.SettingsTabItem.SettingsItems)));
        var items = (view.ItemsSource ?? throw new AssertionException("Missing settings items.")).Cast<SettingsStringItem>().ToArray();
        Assert.That(items, Has.Length.EqualTo(2));
        Assert.That(items[0].HeaderKey is DynamicLocaleKey { Key: "Everything_ExecutablePath_Header" }, Is.True);
        Assert.That(items[0].DescriptionKey is DynamicLocaleKey { Key: "Everything_ExecutablePath_Description" }, Is.True);
        Assert.That(items[1].HeaderKey is DynamicLocaleKey { Key: "Everything_InstanceName_Header" }, Is.True);
        Assert.That(items[1].DescriptionKey is DynamicLocaleKey { Key: "Everything_InstanceName_Description" }, Is.True);
        Assert.That(items[1].Value, Is.EqualTo(expectedInstance));
        items[0].Value = executable;
        items[1].Value = nextInstance;
        return view;
    }

    /// <summary>
    /// Stops only an isolated test-owned instance and awaits its database cleanup.
    /// </summary>
    private static async Task StopAsync(Process process, string executable, string directory, string instanceName)
    {
        using var exit = Start(executable, directory, "-instance", instanceName, "-exit");
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            process.Kill();
            await process.WaitForExitAsync();
        }
    }

    /// <summary>
    /// Reads only host-owned JSON while the production storage's debounced save completes.
    /// </summary>
    private static string? ReadSavedInstanceName(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("InstanceName").GetString();
    }

    /// <summary>
    /// Starts only the test-owned executable without displaying a console window.
    /// </summary>
    private static Process Start(string executable, string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = directory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new AssertionException("Everything test instance did not start.");
    }

    /// <summary>
    /// Reports host-owned diagnostics without preserving collectible exceptions.
    /// </summary>
    private static void AssertApplied(ModApplyResult result)
    {
        Assert.That(
            result.Status,
            Is.EqualTo(ModApplyStatus.Applied),
            string.Join("; ", result.Diagnostics.Select(item => item.Code + ": " + item.Detail)));
    }
}