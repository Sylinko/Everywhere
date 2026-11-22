using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Hosting.Loading;
using Everywhere.Modding.Hosting.Services;
using Everywhere.Modding.Packaging;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShadUI;
using ShadUI.Themes;
using TextMateSharp.Grammars;

namespace Everywhere.Core.Tests.Modding;

/// <summary>
/// Exercises the packaged <c>everywhere.themes</c> built-in mod inside a real collectible
/// <see cref="ModEngine"/> alongside Avalonia's resource system, settings UI, JSON themes,
/// dependent mod registration, and collectible unload cleanup.
/// </summary>
[NonParallelizable]
public sealed class ThemesModTests
{
    private static readonly string[] ExpectedThemesResidentAssemblies = ["Everywhere.Mods.Themes"];
    private static ShadTheme? _sharedShadTheme;

    /// <summary>
    /// Switching between two themes of the same variant (e.g. Dark to Dark), toggling variants,
    /// overriding the primary accent color, and unloading must update and restore resources cleanly.
    /// </summary>
    [AvaloniaTest]
    public async Task PackagedThemes_SwitchSameVariantOverrideAccentAndUnload_AppliesAndRevertsResources()
    {
        EnsureHostThemeInitialized();
        await using var harness = await ThemesHarness.CreateAsync();

        var originalAccent = SystemAccentColors.ColorOverride;
        var originalVariant = Application.Current!.RequestedThemeVariant;
        try
        {
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            AppearanceManager.Shared.UpdateAccentColor(Color.Parse("#0078D7"));

            Assert.That((await harness.Engine.ApplyAsync([harness.ThemesDefinition])).Status, Is.EqualTo(ModApplyStatus.Applied));
            Assert.That(harness.Registry.HasSettings("everywhere.themes"), Is.True);

            // Default Dark preserves ShadUI defaults while AppearanceManager supplies PrimaryColor20.
            Assert.That(TryGetResource<Color>("PrimaryColor20", ThemeVariant.Dark, out var defaultPrimary20), Is.True);
            Assert.That(defaultPrimary20.A, Is.EqualTo(51));

            await ExerciseMainThemesFlowAsync(harness.Registry);

            // Full hot unload removes the overlay dictionary and collects the mod ALC cleanly.
            var unloaded = await harness.Engine.ApplyAsync([]);
            Assert.That(unloaded.Status, Is.EqualTo(ModApplyStatus.Applied), Describe(unloaded));
            Assert.That(unloaded.ResidentAssemblyNames, Is.Empty);
            Assert.That(harness.Registry.HasSettings("everywhere.themes"), Is.False);
            Assert.That(TryGetResource<Color>("WindowBackgroundColor", ThemeVariant.Dark, out var restoredDarkWindow), Is.True);
            Assert.That((restoredDarkWindow.R, restoredDarkWindow.G, restoredDarkWindow.B), Is.EqualTo((0x1A, 0x1A, 0x1A)));
        }
        finally
        {
            CleanupHostThemeResources(originalAccent, originalVariant);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ExerciseMainThemesFlowAsync(ModSettingsRegistry registry)
    {
        var view = (SettingsItemsControl)(registry.CreateView("everywhere.themes")
            ?? throw new AssertionException("Expected Themes settings view."));
        var window = CreateSettingsTestWindow(view);
        window.Show();
        try
        {
            await PumpUiAsync();
            window.UpdateLayout();

            var selectors = view.GetVisualDescendants().OfType<ComboBox>().ToArray();
            Assert.That(selectors, Has.Length.EqualTo(2));

            var lightComboBox = selectors[0];
            var darkComboBox = selectors[1];

            // Select Catppuccin Latte for Light and Catppuccin Mocha for Dark.
            SelectOptionByText(lightComboBox, "Catppuccin Latte");
            SelectOptionByText(darkComboBox, "Catppuccin Mocha");
            await PumpUiAsync();

            Assert.That(TryGetResource<Color>("WindowBackgroundColor", ThemeVariant.Dark, out var mochaWindow), Is.True);
            Assert.That((mochaWindow.R, mochaWindow.G, mochaWindow.B), Is.EqualTo((0x1E, 0x1E, 0x2E)));
            Assert.That(TryGetResource<Color>("PrimaryColor", ThemeVariant.Dark, out var mochaPrimary), Is.True);
            Assert.That(mochaPrimary, Is.EqualTo(Color.Parse("#CBA6F7")));
            Assert.That(TryGetResource<Color>("PrimaryColor20", ThemeVariant.Dark, out var mochaPrimary20), Is.True);
            Assert.That(mochaPrimary20.A, Is.EqualTo(51));
            Assert.That(TryGetResource<Color>("ThemeBackgroundColor", ThemeVariant.Dark, out var mochaThemeBg), Is.True);
            Assert.That(mochaThemeBg, Is.EqualTo(Color.Parse("#181825")));
            Assert.That(TryGetResource<string>("BaseStylesheet", ThemeVariant.Dark, out var mochaCss), Is.True);
            Assert.That(mochaCss, Does.Contain("#CDD6F4"));

            // Same-variant switch (Dark -> Dark: Catppuccin Mocha -> Ayu Dark) updates resources without ActualThemeVariant change.
            SelectOptionByText(darkComboBox, "Ayu Dark");
            await PumpUiAsync();

            Assert.That(TryGetResource<Color>("WindowBackgroundColor", ThemeVariant.Dark, out var ayuWindow), Is.True);
            Assert.That((ayuWindow.R, ayuWindow.G, ayuWindow.B), Is.EqualTo((0x0B, 0x0E, 0x14)));
            Assert.That(TryGetResource<Color>("PrimaryColor", ThemeVariant.Dark, out var ayuPrimary), Is.True);
            Assert.That(ayuPrimary, Is.EqualTo(Color.Parse("#E6B450")));

            // Variant switch (Dark -> Light) activates the Light slot theme.
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            await PumpUiAsync();

            Assert.That(TryGetResource<Color>("WindowBackgroundColor", ThemeVariant.Light, out var latteWindow), Is.True);
            Assert.That((latteWindow.R, latteWindow.G, latteWindow.B), Is.EqualTo((0xEF, 0xF1, 0xF5)));
            Assert.That(TryGetResource<ThemeName>("ColorTheme", ThemeVariant.Light, out var latteCodeTheme), Is.True);
            Assert.That(latteCodeTheme, Is.EqualTo(ThemeName.QuietLight));
            Assert.That(TryGetResource<string>("BaseStylesheet", ThemeVariant.Light, out var latteCss), Is.True);
            Assert.That(latteCss, Does.Contain("#4C4F69"));
            Assert.That(latteCss, Does.Not.Contain("#1A1A1A"));

            // Override accent toggle applies AppearanceManager's global accent color over the active theme.
            var overrideToggle = view.GetVisualDescendants().OfType<ToggleSwitch>().Single();
            overrideToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            SystemAccentColors.ColorOverride = Color.Parse("#FF3366");
            AppearanceManager.Shared.UpdateAccentColor(Color.Parse("#FF3366"));
            await PumpUiAsync();

            Assert.That(TryGetResource<Color>("PrimaryColor", ThemeVariant.Light, out var overriddenPrimary), Is.True);
            Assert.That(overriddenPrimary, Is.EqualTo(Color.Parse("#FF3366")));
            Assert.That(TryGetResource<Color>("WindowBackgroundColor", ThemeVariant.Light, out var preservedWindow), Is.True);
            Assert.That((preservedWindow.R, preservedWindow.G, preservedWindow.B), Is.EqualTo((0xEF, 0xF1, 0xF5)));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// JSON theme files in DataDirectory/themes and third-party dependent mods can register custom themes,
    /// survive malformed JSON files, and clean up registered themes on hot unload.
    /// </summary>
    [AvaloniaTest]
    public async Task PackagedThemes_CustomJsonAndDependentMod_RegistersFallsBackAndCollects()
    {
        EnsureHostThemeInitialized();
        var originalAccent = SystemAccentColors.ColorOverride;
        var originalVariant = Application.Current!.RequestedThemeVariant;
        try
        {
            await using var harness = await ThemesHarness.CreateAsync();

            var customDirectory = Path.Combine(harness.ThemesDefinition.DataDirectory, "themes");
            Directory.CreateDirectory(customDirectory);
            await File.WriteAllTextAsync(Path.Combine(customDirectory, "broken.json"), "{ not-valid-json");
            await File.WriteAllTextAsync(
                Path.Combine(customDirectory, "midnight.json"),
                """
                {
                  "id": "json.midnight",
                  "name": "JSON Midnight",
                  "variant": "dark",
                  "codeTheme": "OneDark",
                  "palette": {
                    "windowBackground": "#101218",
                    "cardBackground": "#1A1E29",
                    "secondaryCardBackground": "#0D0F14",
                    "dialogBackground": "#161A24",
                    "inputBackground": "#0C0E13",
                    "foreground": "#ECEFF4",
                    "mutedForeground": "#8892A6",
                    "border": "#2A3042",
                    "primary": "#5E81AC",
                    "secondary": "#232838",
                    "destructive": "#BF616A",
                    "info": "#81A1C1",
                    "success": "#A3BE8C",
                    "warning": "#EBCB8B"
                  }
                }
                """);

            var dependent = harness.CreateDependentMod("themes.consumer");
            var loaded = await harness.Engine.ApplyAsync([harness.ThemesDefinition, dependent]);
            Assert.That(loaded.Status, Is.EqualTo(ModApplyStatus.Applied), Describe(loaded));

            await ExerciseCustomAndExternalThemesAsync(harness.Registry);

            // Unloading only the dependent mod removes its theme and falls back cleanly without RestartRequired.
            var consumerRemoved = await harness.Engine.ApplyAsync([harness.ThemesDefinition]);
            Assert.Multiple(() =>
            {
                Assert.That(consumerRemoved.Status, Is.EqualTo(ModApplyStatus.Applied), Describe(consumerRemoved));
                Assert.That(consumerRemoved.ResidentAssemblyNames, Is.EqualTo(ExpectedThemesResidentAssemblies));
                Assert.That(TryGetResource<Color>("WindowBackgroundColor", ThemeVariant.Dark, out var fallbackDark), Is.True);
                Assert.That((fallbackDark.R, fallbackDark.G, fallbackDark.B), Is.EqualTo((0x1A, 0x1A, 0x1A)));
            });

            // Unloading the themes mod itself collects cleanly.
            var allRemoved = await harness.Engine.ApplyAsync([]);
            Assert.Multiple(() =>
            {
                Assert.That(allRemoved.Status, Is.EqualTo(ModApplyStatus.Applied), Describe(allRemoved));
                Assert.That(allRemoved.ResidentAssemblyNames, Is.Empty);
            });
        }
        finally
        {
            CleanupHostThemeResources(originalAccent, originalVariant);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ExerciseCustomAndExternalThemesAsync(ModSettingsRegistry registry)
    {
        var view = (SettingsItemsControl)(registry.CreateView("everywhere.themes")
            ?? throw new AssertionException("Expected Themes settings view."));
        var window = CreateSettingsTestWindow(view);
        window.Show();
        try
        {
            await PumpUiAsync();
            window.UpdateLayout();

            var darkComboBox = view.GetVisualDescendants().OfType<ComboBox>().ElementAt(1);

            // Both JSON and dependent-mod themes are available and selectable.
            SelectOptionByText(darkComboBox, "JSON Midnight");
            await PumpUiAsync();
            Assert.That(TryGetResource<Color>("PrimaryColor", ThemeVariant.Dark, out var jsonPrimary), Is.True);
            Assert.That(jsonPrimary, Is.EqualTo(Color.Parse("#5E81AC")));
            Assert.That(TryGetResource<ThemeName>("ColorTheme", ThemeVariant.Dark, out var jsonCodeTheme), Is.True);
            Assert.That(jsonCodeTheme, Is.EqualTo(ThemeName.OneDark));

            SelectOptionByText(darkComboBox, "External Mod Solar");
            await PumpUiAsync();
            Assert.That(TryGetResource<Color>("PrimaryColor", ThemeVariant.Dark, out var modPrimary), Is.True);
            Assert.That(modPrimary, Is.EqualTo(Color.Parse("#2AA198")));
        }
        finally
        {
            window.Close();
        }
    }

    private static SettingsEditorTestWindow CreateSettingsTestWindow(SettingsItemsControl view)
    {
        var window = new SettingsEditorTestWindow(view);
        var baseUri = new Uri("avares://Everywhere.Core/");
        window.Resources.MergedDictionaries.Add(
            new ResourceInclude(baseUri)
            {
                Source = new Uri(baseUri, "Views/Configuration/SettingsGroupPanel.axaml"),
            });
        if (window.Resources[typeof(SettingsGroupPanel)] is ControlTheme groupTheme)
        {
            window.Styles.Add(
                new Style(selector => selector.OfType<SettingsGroupPanel>())
                {
                    Setters = { new Setter(StyledElement.ThemeProperty, groupTheme) },
                });
        }

        return window;
    }

    private static void EnsureHostThemeInitialized()
    {
        var app = Application.Current ?? throw new InvalidOperationException("Avalonia Application is not initialized.");
        if (!app.Styles.OfType<ShadTheme>().Any())
        {
            _sharedShadTheme ??= new ShadTheme();
            app.Styles.Add(_sharedShadTheme);
        }

        if (app.Resources is not ResourceDictionary rootDictionary)
        {
            rootDictionary = new ResourceDictionary();
            app.Resources = rootDictionary;
        }

        AppearanceManager appearanceManager;
        try
        {
            appearanceManager = AppearanceManager.Shared;
        }
        catch (InvalidOperationException)
        {
            appearanceManager = new AppearanceManager();
        }

        if (!rootDictionary.MergedDictionaries.Contains(appearanceManager))
        {
            rootDictionary.MergedDictionaries.Add(appearanceManager);
        }

        appearanceManager.UpdateAccentColor(null);
    }

    private static void CleanupHostThemeResources(Color? originalAccent, ThemeVariant? originalVariant)
    {
        SystemAccentColors.ColorOverride = originalAccent;
        AppearanceManager.Shared.UpdateAccentColor(originalAccent);

        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = originalVariant;
        if (_sharedShadTheme is not null)
        {
            app.Styles.Remove(_sharedShadTheme);
        }

        if (app.Resources is ResourceDictionary rootDictionary)
        {
            rootDictionary.MergedDictionaries.Remove(AppearanceManager.Shared);
        }
    }

    private static void SelectOptionByText(ComboBox comboBox, string expectedText)
    {
        var items = comboBox.ItemsSource?.Cast<object>().ToArray()
            ?? throw new AssertionException("ComboBox ItemsSource is empty.");

        var match = items.FirstOrDefault(item =>
            item.GetType().GetProperty("DisplayNameKey")?.GetValue(item) is IDynamicLocaleKey key &&
            string.Equals(key.ToString(), expectedText, StringComparison.Ordinal));

        Assert.That(match, Is.Not.Null, $"Expected option '{expectedText}' in theme selector.");
        comboBox.SelectedItem = match;
    }

    private static bool TryGetResource<TValue>(object key, ThemeVariant variant, out TValue value)
    {
        if (Application.Current?.TryFindResource(key, variant, out var raw) is true && raw is TValue typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    private static async Task PumpUiAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static string Describe(ModApplyResult result) =>
        string.Join("; ", result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}:{diagnostic.Detail}"));

    private sealed class ThemesHarness : IAsyncDisposable
    {
        public ModEngine Engine { get; }
        public ModSettingsRegistry Registry { get; }
        public ModDefinition ThemesDefinition { get; }

        private readonly string _root;
        private readonly HostServiceContainer _services;

        private ThemesHarness(
            string root,
            HostServiceContainer services,
            ModSettingsRegistry registry,
            ModEngine engine,
            ModDefinition themesDefinition)
        {
            _root = root;
            _services = services;
            Registry = registry;
            Engine = engine;
            ThemesDefinition = themesDefinition;
        }

        public static async Task<ThemesHarness> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "Everywhere.Themes.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var packagePath = Path.Combine(AppContext.BaseDirectory, "themes-mod", "everywhere.themes.zip");
            Assert.That(File.Exists(packagePath), Is.True, $"Missing packaged themes mod at '{packagePath}'.");

            var store = new ModPackageStore(
                Path.Combine(root, "installed"),
                Path.Combine(root, "data"),
                Path.Combine(root, "staging"));

            ModInstallation installation;
            await using (var archive = File.OpenRead(packagePath))
            {
                installation = await store.ImportAsync(archive);
            }

            var registry = new ModSettingsRegistry();
            var services = new HostServiceContainer(
                new ServiceCollection()
                    .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
                    .AddSingleton<IModSettingsRegistry>(registry));

            var testAssembly = typeof(ThemesModTests).Assembly.Location;
            var hostFiles = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
                .Append(typeof(Porta.Pty.IPtyConnection).Assembly.Location)
                .Where(path => path != testAssembly && IsManaged(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            var engine = new ModEngine(
                new HostAssemblyCatalog(hostFiles),
                services,
                new ModEngineOptions
                {
                    CollectionAttempts = 30,
                    CollectionDelay = TimeSpan.FromMilliseconds(50),
                });

            var definition = new ModDefinition(
                installation,
                store.GetDataDirectory(installation),
                RuntimeInformation.RuntimeIdentifier,
                "1.0.0",
                "1.0.0",
                "net11.0");

            return new ThemesHarness(root, services, registry, engine, definition);
        }

        public ModDefinition CreateDependentMod(string id)
        {
            var package = Path.Combine(_root, id);
            Directory.CreateDirectory(package);
            var assembly = typeof(ThemesModTests).Assembly.Location;
            var fileName = Path.GetFileName(assembly);
            File.Copy(assembly, Path.Combine(package, fileName));

            var manifest = new ModManifest
            {
                Id = id,
                Version = "1.0.0",
                Entry = new UniversalModEntry
                {
                    Assembly = fileName,
                    Type = typeof(DependentThemeFixtureMod).FullName!,
                    Assemblies = [fileName],
                    Framework = "net11.0",
                    ModApiVersion = "[1.0.0,2.0.0)",
                    Dependencies =
                    [
                        new ModDependency
                        {
                            Id = "everywhere.themes",
                            Version = "[1.0.0,)",
                            IsOptional = false,
                            Assemblies = ["Everywhere.Mods.Themes"],
                        },
                    ],
                },
            };
            ModManifestFile.Write(package, manifest);

            var data = Path.Combine(_root, "data", id);
            Directory.CreateDirectory(data);
            return new ModDefinition(
                new ModInstallation { InstallationId = id, Directory = package, Manifest = manifest },
                data,
                RuntimeInformation.RuntimeIdentifier,
                "1.0.0",
                "1.0.0",
                "net11.0");
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await _services.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

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
    }
}

/// <summary>
/// Loaded inside an isolated collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/> by
/// <see cref="ThemesModTests"/> to exercise cross-mod theme registration without a compile-time dependency.
/// </summary>
public sealed class DependentThemeFixtureMod : IMod
{
    /// <inheritdoc />
    public ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken)
    {
        var themesAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(static assembly => string.Equals(assembly.GetName().Name, "Everywhere.Mods.Themes", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Everywhere.Mods.Themes assembly was not loaded for dependent mod.");

        var registryType = themesAssembly.GetType("Everywhere.Mods.Themes.ThemeRegistry", throwOnError: true)!;
        var definitionType = themesAssembly.GetType("Everywhere.Mods.Themes.ThemeDefinition", throwOnError: true)!;
        var paletteType = themesAssembly.GetType("Everywhere.Mods.Themes.ThemePalette", throwOnError: true)!;
        var variantType = themesAssembly.GetType("Everywhere.Mods.Themes.ThemeBaseVariant", throwOnError: true)!;

        var palette = Activator.CreateInstance(paletteType)!;
        SetProperty(palette, "WindowBackground", Color.Parse("#002B36"));
        SetProperty(palette, "CardBackground", Color.Parse("#073642"));
        SetProperty(palette, "SecondaryCardBackground", Color.Parse("#00212B"));
        SetProperty(palette, "DialogBackground", Color.Parse("#073642"));
        SetProperty(palette, "InputBackground", Color.Parse("#001E26"));
        SetProperty(palette, "Foreground", Color.Parse("#93A1A1"));
        SetProperty(palette, "MutedForeground", Color.Parse("#657B83"));
        SetProperty(palette, "Border", Color.Parse("#294C57"));
        SetProperty(palette, "Primary", Color.Parse("#2AA198"));
        SetProperty(palette, "Secondary", Color.Parse("#094150"));
        SetProperty(palette, "Destructive", Color.Parse("#DC322F"));
        SetProperty(palette, "Info", Color.Parse("#268BD2"));
        SetProperty(palette, "Success", Color.Parse("#859900"));
        SetProperty(palette, "Warning", Color.Parse("#B58900"));

        var definition = Activator.CreateInstance(definitionType)!;
        SetProperty(definition, "Id", "external.solar");
        SetProperty(definition, "DisplayName", "External Mod Solar");
        SetProperty(definition, "Variant", Enum.Parse(variantType, "Dark"));
        SetProperty(definition, "Palette", palette);

        var registerMethod = registryType.GetMethod("RegisterTheme", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException("ThemeRegistry.RegisterTheme");
        var handle = registerMethod.Invoke(null, [definition])
            ?? throw new InvalidOperationException("RegisterTheme returned null.");

        context.Register(handle);
        return ValueTask.CompletedTask;
    }

    private static void SetProperty(object instance, string name, object value)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(instance.GetType().FullName, name);
        property.SetValue(instance, value);
    }
}
