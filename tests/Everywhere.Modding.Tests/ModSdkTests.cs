using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using System.Text.Json;
using System.Xml.Linq;
using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Tests;

/// <summary>
/// Builds real author projects through SDK imports and checks their generated distribution artifacts.
/// </summary>
public sealed class ModSdkTests
{
    private string _root = "";
    private string _repository = "";
    private string _configuration = "";

    /// <summary>
    /// Creates an independent author workspace outside the repository's build property inheritance.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "Everywhere.Modding.Sdk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new AssertionException("Cannot identify the test build configuration.");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (!File.Exists(Path.Combine(directory.FullName, "Everywhere.slnx")))
        {
            directory = directory.Parent ?? throw new AssertionException("Cannot locate the repository.");
        }

        _repository = directory.FullName;
    }

    /// <summary>
    /// Deletes only the independent author workspace owned by this test.
    /// </summary>
    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Both compilation paths honor standard NuGet Pack/PackagePath semantics and exclude host assemblies and opt-out files.
    /// Source references must also keep command-line mod identity and output paths out of the host build.
    /// </summary>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task Pack_HostImport_PreservesPresentationAndExcludesHostFiles(bool isSourceImport, bool hasImplementationReference)
    {
        var hostProject = Path.Combine(_repository, "tests", "Everywhere.Modding.Tests", "Fixtures", "Host", "ModFixture.Host.csproj");
        var sdk = Path.Combine(_repository, "src", "Everywhere.Modding.Sdk");
        var tools = Path.Combine(sdk, "bin", _configuration, "net11.0");
        Assert.That(File.Exists(Path.Combine(tools, "NuGet.Frameworks.dll")), Is.True, "SDK tools directory must include NuGet.Frameworks.dll.");

        var exported = await RunAsync(
            _repository,
            "msbuild",
            hostProject,
            "-target:EverywhereExportModHost",
            "-property:Configuration=" + _configuration,
            "-getTargetResult:EverywhereExportModHost",
            "-nologo",
            "-verbosity:quiet");
        Assert.That(exported.ExitCode, Is.Zero, exported.Output);
        using var export = JsonDocument.Parse(exported.Output);
        var descriptor = export.RootElement.GetProperty("TargetResults")
            .GetProperty("EverywhereExportModHost")
            .GetProperty("Items")[0]
            .GetProperty("Identity")
            .GetString();
        var hostDirectory = Path.GetDirectoryName(descriptor) ?? throw new AssertionException("Missing host export directory.");
        var host = isSourceImport ? hostProject : hostDirectory;
        var project = $"""
                       <Project>
                         <Import Project="{Escape(Path.Combine(sdk, "Sdk", "Sdk.props"))}" />
                         <PropertyGroup>
                           <ImplicitUsings>enable</ImplicitUsings>
                           <Nullable>enable</Nullable>
                           <EverywhereHost>{Escape(host)}</EverywhereHost>
                           <EverywhereModToolsDirectory>{Escape(tools)}</EverywhereModToolsDirectory>
                           <EverywhereModId>fixture.sdk</EverywhereModId>
                           <EverywhereModEntryType>SdkFixture.Entry</EverywhereModEntryType>
                           <EverywhereHostVersion>{(hasImplementationReference ? "[0.9.0,0.10.0)" : "")}</EverywhereHostVersion>
                         </PropertyGroup>
                         <ItemGroup>
                           <EverywhereModLocalization Include="EN" Name="{(hasImplementationReference ? "Tools &amp; Search" : "")}" Description="A &amp; B | C; &quot;quoted&quot; &apos;text&apos; it&apos;s (fine)&#10;Second line" />
                           <EverywhereModLocalization Include="zh-Hans" Description="搜索本地文件" Readme="docs/中文指南.md" />
                           <Content Include="shared/中文指南.md" PackagePath="docs/中文指南.md" />
                           <Content Include="docs/images/example.txt" />
                           <Content Include="docs/sample.dll" />
                           <Content Include="local-debug.json" CopyToOutputDirectory="PreserveNewest" Pack="false" />
                           <None Update="README.draft.md" Pack="false" />
                           <Reference Include="ModFixture.B" HintPath="{Escape(Path.Combine(_repository, "tests", "Everywhere.Modding.Tests", "Fixtures", "B", "bin", _configuration, "net11.0", "ModFixture.B.dll"))}" />
                         </ItemGroup>
                         <Import Project="{Escape(Path.Combine(sdk, "Sdk", "Sdk.targets"))}" />
                         <Target Name="CorruptEntryForPackaging" AfterTargets="Build" Condition="'$(CorruptEntry)' == 'true'">
                           <WriteLinesToFile File="$(TargetPath)" Lines="truncated build output" Overwrite="true" />
                         </Target>
                       </Project>
                       """;
        await File.WriteAllTextAsync(Path.Combine(_root, "Mod.csproj"), project);
        Directory.CreateDirectory(Path.Combine(_root, "docs", "images"));
        Directory.CreateDirectory(Path.Combine(_root, "shared"));
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "# English");
        await File.WriteAllTextAsync(Path.Combine(_root, "README.zh-Hans.md"), "# Conventional Chinese");
        await File.WriteAllTextAsync(Path.Combine(_root, "README.draft.md"), "# Draft notes");
        await File.WriteAllTextAsync(Path.Combine(_root, "local-debug.json"), "{\"debug\":true}");
        await File.WriteAllTextAsync(Path.Combine(_root, "shared", "中文指南.md"), "# 中文\n![example](images/example.txt)");
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "images", "example.txt"), "attachment");
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "sample.dll"), "A documentation sample, not a loadable library.");
        await File.WriteAllTextAsync(Path.Combine(_root, "Strings.resx"), "<root><data name=\"Greeting\"><value>Hello</value></data></root>");
        await File.WriteAllTextAsync(Path.Combine(_root, "Strings.fr.resx"), "<root><data name=\"Greeting\"><value>Bonjour</value></data></root>");
        var body = hasImplementationReference ? "GC.KeepAlive(ModFixture.Host.HostApi.Name); GC.KeepAlive(new DiffPlex.Differ());" : "";
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Entry.cs"),
            $$"""
              using Everywhere.Modding.Abstractions;
              namespace SdkFixture;
              public sealed class Entry : IMod
              {
                  public ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken)
                  {
                      {{body}}
                      return ValueTask.CompletedTask;
                  }
              }
              """);
        var outputDirectory = Path.Combine(_root, "mod-output");
        var result = await RunAsync(
            _root,
            "msbuild",
            "Mod.csproj",
            "-restore",
            "-target:EverywherePack",
            "-nologo",
            "-verbosity:minimal",
            "-property:Version=1.2.3",
            "-property:OutputPath=" + outputDirectory,
            "-property:EverywhereHost=" + host);
        Assert.That(result.ExitCode, Is.Zero, result.Output);
        var packageResult = result.Output.Split('\n')
            .Select(line => line.Trim())
            .Single(line => line.StartsWith("{\"code\":\"PackageCreated\"", StringComparison.Ordinal));
        using var packageDocument = JsonDocument.Parse(packageResult);
        Assert.That(packageDocument.RootElement.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "code", "path" }));
        Assert.That(
            packageDocument.RootElement.GetProperty("path").GetString(),
            Is.EqualTo(Path.Combine(outputDirectory, "fixture.sdk-1.2.3.zip")));

        var unpacked = Path.Combine(_root, "unpacked-main");
        await ZipFile.ExtractToDirectoryAsync(Path.Combine(outputDirectory, "fixture.sdk-1.2.3.zip"), unpacked);
        var parsedManifest = ModManifestFile.Read(unpacked);

        await using (var archive = await ZipFile.OpenReadAsync(Path.Combine(outputDirectory, "fixture.sdk-1.2.3.zip")))
        {
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Contain("Mod.dll"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Contain("fr/Mod.resources.dll"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Contain("fr/ModFixture.B.resources.dll"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Contain("docs/sample.dll"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Contain("docs/中文指南.md"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Contain("docs/images/example.txt"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Modding.Abstractions.dll"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Versioning.dll"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Modding.Packaging.dll"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("ModFixture.Host.dll"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("DiffPlex.dll"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("host-content.txt"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Modding.Abstractions.pdb"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Modding.Abstractions.xml"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("local-debug.json"));
            Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("README.draft.md"));
            Assert.That(archive.GetEntry("README.md"), Is.Not.Null);
            Assert.That(archive.GetEntry("README.zh-Hans.md"), Is.Null);
        }

        Assert.That(File.Exists(Path.Combine(outputDirectory, "local-debug.json")), Is.True);

        using var importedHost = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(_root, "obj", "Debug", "net11.0", "everywhere-host-references.txt.host.json")));
        Assert.That(importedHost.RootElement.GetProperty("HostVersion").GetString(), Is.EqualTo("0.9.2"));
        Assert.That(File.Exists(Path.Combine(outputDirectory, "mod-host.json")), Is.False);

        var selectedEntry = ModPlatformSelector.Select(parsedManifest, "win-x64");
        Assert.Multiple(() =>
        {
            Assert.That(parsedManifest.Entry, Is.TypeOf<UniversalModEntry>());
            Assert.That(
                selectedEntry?.Assemblies,
                Does.Contain("ModFixture.B.dll").And.Contain("fr/ModFixture.B.resources.dll").And.Not.Contain("docs/sample.dll"));
            Assert.That(selectedEntry?.ModApiVersion, Is.EqualTo("[1.0.0,2.0.0-0)"));
            Assert.That(selectedEntry?.HostVersion, Is.EqualTo(hasImplementationReference ? "[0.9.0,0.10.0)" : null));
            Assert.That(File.Exists(Path.Combine(hostDirectory, "ModFixture.Host.xml")), Is.True);
            Assert.That(parsedManifest.Name.Count, Is.EqualTo(hasImplementationReference ? 1 : 0));
            if (hasImplementationReference) Assert.That(parsedManifest.Name["EN"], Is.EqualTo("Tools & Search"));
            Assert.That(parsedManifest.Description["ZH-HANS"], Is.EqualTo("搜索本地文件"));
            Assert.That(parsedManifest.Description["EN"], Is.EqualTo("A & B | C; \"quoted\" 'text' it's (fine)\nSecond line"));
            Assert.That(parsedManifest.Readme["EN"], Is.EqualTo("README.md"));
            Assert.That(parsedManifest.Readme["zh-hans"], Is.EqualTo("docs/中文指南.md"));
            Assert.That(parsedManifest.Readme.ContainsKey("draft"), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(unpacked, parsedManifest.Readme["EN"])), Is.EqualTo("# English"));
        });

        if (!isSourceImport && !hasImplementationReference)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, "README.en.md"), "# Other English");
            var ambiguous = await RunAsync(_root, "msbuild", "Mod.csproj", "-target:EverywherePack", "-nologo", "-verbosity:minimal");
            Assert.That(ambiguous.ExitCode, Is.Not.Zero);
            Assert.That(ambiguous.Output, Does.Contain("DuplicateTranslation"));

            await File.WriteAllTextAsync(
                Path.Combine(_root, "Mod.csproj"),
                project.Replace(
                    "Include=\"EN\" Name=",
                    "Include=\"EN\" Readme=\"README.en.md\" Name=",
                    StringComparison.Ordinal));
            var explicitReadme = await RunAsync(_root, "msbuild", "Mod.csproj", "-target:EverywherePack", "-nologo", "-verbosity:minimal");
            Assert.That(explicitReadme.ExitCode, Is.Zero, explicitReadme.Output);

            // A project with no localization declarations still packages its conventional READMEs (honoring Pack=false).
            File.Delete(Path.Combine(_root, "README.en.md"));
            var plainProject = XDocument.Parse(project);
            plainProject.Descendants("EverywhereModLocalization").Remove();
            await File.WriteAllTextAsync(Path.Combine(_root, "Mod.csproj"), plainProject.ToString());
            var plain = await RunAsync(_root, "msbuild", "Mod.csproj", "-target:EverywherePack", "-nologo", "-verbosity:minimal");
            Assert.That(plain.ExitCode, Is.Zero, plain.Output);
            var defaultArchive = Path.Combine(_root, "bin", "Debug", "net11.0", "fixture.sdk-1.0.0.zip");
            var plainDirectory = Path.Combine(_root, "plain");
            await ZipFile.ExtractToDirectoryAsync(defaultArchive, plainDirectory);
            var plainManifest = ModManifestFile.Read(plainDirectory);
            Assert.That(plainManifest.Name, Is.Empty);
            Assert.That(plainManifest.Description, Is.Empty);
            Assert.That(plainManifest.Readme.Keys, Is.EquivalentTo(new[] { "en", "zh-hans" }));

            // Explicitly mapping README.md to Chinese must not also infer an English mapping.
            plainProject.Root?.Add(
                new XElement(
                    "ItemGroup",
                    new XElement("EverywhereModLocalization", new XAttribute("Include", "zh-Hans"), new XAttribute("Readme", "README.md"))));
            await File.WriteAllTextAsync(Path.Combine(_root, "Mod.csproj"), plainProject.ToString());
            var remapped = await RunAsync(_root, "msbuild", "Mod.csproj", "-target:EverywherePack", "-nologo", "-verbosity:minimal");
            Assert.That(remapped.ExitCode, Is.Zero, remapped.Output);
            var remappedDirectory = Path.Combine(_root, "remapped");
            await ZipFile.ExtractToDirectoryAsync(defaultArchive, remappedDirectory);
            var remappedManifest = ModManifestFile.Read(remappedDirectory);
            Assert.That(remappedManifest.Name, Is.Empty);
            Assert.That(remappedManifest.Readme.Keys, Is.EquivalentTo(new[] { "zh-hans" }));
            Assert.That(remappedManifest.Readme["zh-hans"], Is.EqualTo("README.md"));

            // PackagePath can rename the same source file inside the package without creating an English translation.
            plainProject.Descendants("EverywhereModLocalization").Single().SetAttributeValue("Readme", "docs/guide.md");
            plainProject.Root?.Add(
                new XElement(
                    "ItemGroup",
                    new XElement(
                        "Content",
                        new XAttribute("Include", "README.md"),
                        new XAttribute("PackagePath", "docs/guide.md"))));
            await File.WriteAllTextAsync(Path.Combine(_root, "Mod.csproj"), plainProject.ToString());
            var linked = await RunAsync(_root, "msbuild", "Mod.csproj", "-target:EverywherePack", "-nologo", "-verbosity:minimal");
            Assert.That(linked.ExitCode, Is.Zero, linked.Output);
            var linkedDirectory = Path.Combine(_root, "linked");
            await ZipFile.ExtractToDirectoryAsync(defaultArchive, linkedDirectory);
            var linkedManifest = ModManifestFile.Read(linkedDirectory);
            Assert.That(linkedManifest.Readme.Keys, Is.EquivalentTo(new[] { "zh-hans" }));
            Assert.That(linkedManifest.Readme["zh-hans"], Is.EqualTo("docs/guide.md"));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(linkedDirectory, "docs", "guide.md")), Is.EqualTo("# English"));

            // A damaged compiler output must fail as managed input, not become a passive package file.
            var damaged = await RunAsync(
                _root,
                "msbuild",
                "Mod.csproj",
                "-target:EverywherePack",
                "-property:CorruptEntry=true",
                "-nologo",
                "-verbosity:minimal");
            Assert.That(damaged.ExitCode, Is.Not.Zero);
            Assert.That(damaged.Output, Does.Contain("SdkOperationFailed"));
        }
    }

    /// <summary>
    /// A real SDK command and runtime compatibility helper agree on prerelease host ranges and exact API defaults.
    /// </summary>
    [Test]
    public async Task Pack_PrereleaseHostAndContract_AgreesWithCompatibility()
    {
        var tools = Path.Combine(_repository, "src", "Everywhere.Modding.Sdk", "bin", _configuration, "net11.0", "Everywhere.Modding.Sdk.dll");
        var references = Path.Combine(_root, "references.txt");
        var packDirectory = Path.Combine(_root, "pack-input");
        Directory.CreateDirectory(packDirectory);
        var archivePath = Path.Combine(_root, "mod.zip");
        var assembly = Path.Combine(AppContext.BaseDirectory, "fixtures", "ModFixture.B.dll");
        var contract = typeof(IMod).Assembly.Location;
        var host = new ModHostDescriptor
        {
            HostVersion = "0.9.0-canary.20261016.10",
            ModApiVersion = "1.1.0-preview.10",
            Framework = "net11.0",
            RuntimeIdentifier = "win-x64",
            EntryAssembly = "Host.dll"
        };
        await File.WriteAllLinesAsync(references, [contract]);
        await File.WriteAllLinesAsync(references + ".runtime.txt", [contract]);
        await File.WriteAllTextAsync(references + ".host.json", JsonSerializer.Serialize(host));
        await File.WriteAllTextAsync(Path.Combine(packDirectory, "payload.txt"), string.Empty);
        await WritePackPropertiesAsync(
            Path.Combine(packDirectory, "properties.txt"),
            assembly,
            references,
            "fixture.preview",
            "1.0.0-preview.2+commit",
            "ModFixture.Entry",
            "[0.9.0-canary.20261016.2,0.9.0)",
            archivePath);

        var result = await RunAsync(_root, tools, "pack", packDirectory);
        Assert.That(result.ExitCode, Is.Zero, result.Output);

        var directory = Path.Combine(_root, "unpacked");
        await ZipFile.ExtractToDirectoryAsync(archivePath, directory);
        var manifest = ModManifestFile.Read(directory);
        var entry = ModPlatformSelector.Select(manifest, host.RuntimeIdentifier);
        Assert.Multiple(() =>
        {
            Assert.That(entry, Is.Not.Null);
            Assert.That(entry?.ModApiVersion, Is.EqualTo("[1.1.0-preview.10]"));
            Assert.That(manifest.Version, Is.EqualTo("1.0.0-preview.2+commit"));
            Assert.That(entry?.HostVersion, Is.EqualTo("[0.9.0-canary.20261016.2,0.9.0)"));
            Assert.That(ModCompatibility.IsSatisfied(entry!.ModApiVersion, host.ModApiVersion), Is.True);
            Assert.That(ModCompatibility.IsSatisfied(entry.ModApiVersion, "1.1.0-preview.11"), Is.False);
            Assert.That(ModCompatibility.IsSatisfied(entry.HostVersion!, host.HostVersion), Is.True);
        });

        await WritePackPropertiesAsync(
            Path.Combine(packDirectory, "properties.txt"),
            assembly,
            references,
            "fixture.preview",
            "1.0.0-preview.2",
            "ModFixture.Entry",
            "[0.9.0,0.10.0)",
            Path.Combine(_root, "invalid.zip"));
        var invalid = await RunAsync(_root, tools, "pack", packDirectory);
        Assert.That(invalid.ExitCode, Is.Not.Zero);
        Assert.That(invalid.Output, Does.Contain("BuildHostOutsideRange"));
    }

    /// <summary>
    /// Unchanged pack inputs skip re-zipping and preserve archive timestamps, while changed inputs regenerate the package.
    /// </summary>
    [Test]
    public async Task Pack_WhenInputsAreUnchanged_PreservesArchiveAndRegeneratesOnChange()
    {
        var tools = Path.Combine(_repository, "src", "Everywhere.Modding.Sdk", "bin", _configuration, "net11.0", "Everywhere.Modding.Sdk.dll");
        var references = Path.Combine(_root, "references.txt");
        var packDirectory = Path.Combine(_root, "incremental-pack");
        Directory.CreateDirectory(packDirectory);
        var archivePath = Path.Combine(_root, "incremental.zip");
        var assembly = Path.Combine(AppContext.BaseDirectory, "fixtures", "ModFixture.B.dll");
        var readmePath = Path.Combine(_root, "README.md");
        await File.WriteAllTextAsync(readmePath, "# Initial");
        var contract = typeof(IMod).Assembly.Location;
        var host = new ModHostDescriptor
        {
            HostVersion = "0.9.0",
            ModApiVersion = "1.0.0",
            Framework = "net11.0",
            RuntimeIdentifier = "win-x64",
            EntryAssembly = "Host.dll"
        };
        await File.WriteAllLinesAsync(references, [contract]);
        await File.WriteAllLinesAsync(references + ".runtime.txt", [contract]);
        await File.WriteAllTextAsync(references + ".host.json", JsonSerializer.Serialize(host));
        await File.WriteAllTextAsync(Path.Combine(packDirectory, "payload.txt"), $"{readmePath}|README.md|pack-file||");
        await File.WriteAllTextAsync(Path.Combine(packDirectory, "readmes.txt"), "README.md");
        await WritePackPropertiesAsync(
            Path.Combine(packDirectory, "properties.txt"),
            assembly,
            references,
            "fixture.incremental",
            "1.0.0",
            "ModFixture.Entry",
            "[0.9.0,0.10.0)",
            archivePath);

        var first = await RunAsync(_root, tools, "pack", packDirectory);
        Assert.That(first.ExitCode, Is.Zero, first.Output);

        var preservedTimestamp = DateTime.UtcNow.AddDays(-10);
        File.SetLastWriteTimeUtc(archivePath, preservedTimestamp);
        var expectedTimestamp = File.GetLastWriteTimeUtc(archivePath);

        var second = await RunAsync(_root, tools, "pack", packDirectory);
        Assert.That(second.ExitCode, Is.Zero, second.Output);
        Assert.That(File.GetLastWriteTimeUtc(archivePath), Is.EqualTo(expectedTimestamp));

        await File.WriteAllTextAsync(readmePath, "# Updated content");
        File.SetLastWriteTimeUtc(readmePath, DateTime.UtcNow.AddMinutes(1));
        var third = await RunAsync(_root, tools, "pack", packDirectory);
        Assert.That(third.ExitCode, Is.Zero, third.Output);
        Assert.That(File.GetLastWriteTimeUtc(archivePath), Is.GreaterThan(expectedTimestamp));

        var unpacked = Path.Combine(_root, "incremental-unpacked");
        await ZipFile.ExtractToDirectoryAsync(archivePath, unpacked);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(unpacked, "README.md")), Is.EqualTo("# Updated content"));
    }

    /// <summary>
    /// Universal mods can bundle multi-platform native files in runtimes/{rid}/native/, strip declared dependency assemblies,
    /// and generate signed static indexes across both layouts.
    /// </summary>
    [TestCase("directory")]
    [TestCase("release")]
    public async Task Pack_AuthorLicenseDependenciesAndBuildIndex_GeneratesSignedRepository(string layout)
    {
        var hostProject = Path.Combine(_repository, "tests", "Everywhere.Modding.Tests", "Fixtures", "Host", "ModFixture.Host.csproj");
        var sdk = Path.Combine(_repository, "src", "Everywhere.Modding.Sdk");
        var toolsDir = Path.Combine(sdk, "bin", _configuration, "net11.0");
        var tools = Path.Combine(toolsDir, "Everywhere.Modding.Sdk.dll");
        var exported = await RunAsync(
            _repository,
            "msbuild",
            hostProject,
            "-target:EverywhereExportModHost",
            "-property:Configuration=" + _configuration,
            "-getTargetResult:EverywhereExportModHost",
            "-nologo",
            "-verbosity:quiet");
        Assert.That(exported.ExitCode, Is.Zero, exported.Output);
        using var export = JsonDocument.Parse(exported.Output);
        var descriptor = export.RootElement.GetProperty("TargetResults")
            .GetProperty("EverywhereExportModHost")
            .GetProperty("Items")[0]
            .GetProperty("Identity")
            .GetString();
        var hostDirectory = Path.GetDirectoryName(descriptor) ?? throw new AssertionException("Missing host export directory.");

        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
        Directory.CreateDirectory(Path.Combine(_root, "Licenses"));
        Directory.CreateDirectory(Path.Combine(_root, "native"));
        Directory.CreateDirectory(Path.Combine(_root, "extra"));
        Directory.CreateDirectory(Path.Combine(_root, "OtherCore"));

        await File.WriteAllTextAsync(Path.Combine(_root, "Assets", "icon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        await File.WriteAllTextAsync(Path.Combine(_root, "LICENSE"), "Proprietary test license.");
        await File.WriteAllTextAsync(Path.Combine(_root, "Licenses", "DependencyA.txt"), "MIT dependency notice.");
        await File.WriteAllTextAsync(Path.Combine(_root, "native", "hook.dll"), "win-native");
        await File.WriteAllTextAsync(Path.Combine(_root, "native", "libhook.so.1"), "linux-native");
        await File.WriteAllTextAsync(Path.Combine(_root, "native", "custom_bridge.pyd"), "osx-native-extension");
        await File.WriteAllTextAsync(Path.Combine(_root, "extra", "manual.txt"), "manual content");
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "# Guide");

        await File.WriteAllTextAsync(
            Path.Combine(_root, "OtherCore", "Other.Core.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "OtherCore", "CoreApi.cs"),
            "namespace Other.Core; public static class CoreApi { public static string Ping() => \"pong\"; }");

        await File.WriteAllTextAsync(
            Path.Combine(_root, "Entry.cs"),
            """
            using Everywhere.Modding.Abstractions;
            namespace SdkMetaFixture;
            public sealed class Entry : IMod
            {
                public ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken)
                {
                    GC.KeepAlive(ModFixture.Host.HostApi.Name);
                    GC.KeepAlive(Other.Core.CoreApi.Ping());
                    return ValueTask.CompletedTask;
                }
            }
            """);

        var extraAssembly = Path.Combine(AppContext.BaseDirectory, "fixtures", "ModFixture.B.dll");
        var project = $"""
                       <Project>
                         <Import Project="{Escape(Path.Combine(sdk, "Sdk", "Sdk.props"))}" />
                         <PropertyGroup>
                           <ImplicitUsings>enable</ImplicitUsings>
                           <Nullable>enable</Nullable>
                           <EverywhereHost>{Escape(hostDirectory)}</EverywhereHost>
                           <EverywhereModToolsDirectory>{Escape(toolsDir)}</EverywhereModToolsDirectory>
                           <EverywhereModId>fixture.meta</EverywhereModId>
                           <EverywhereModEntryType>SdkMetaFixture.Entry</EverywhereModEntryType>
                           <EverywhereHostVersion>[0.9.0,0.10.0)</EverywhereHostVersion>
                           <EverywhereModAuthor>O'Connor; Sons</EverywhereModAuthor>
                           <EverywhereModHomepage>https://example.com/fixture</EverywhereModHomepage>
                           <EverywhereModRepository>https://github.com/example/fixture?ref=main</EverywhereModRepository>
                           <EverywhereModRepositoryDirectory>mods/fixture</EverywhereModRepositoryDirectory>
                           <EverywhereModIcon>Assets/icon.svg</EverywhereModIcon>
                           <EverywhereModLicense>LicenseRef-TestProprietary</EverywhereModLicense>
                           <EverywhereModLicenseFile>LICENSE</EverywhereModLicenseFile>
                           <EverywhereModRequireLicenseAcceptance>true</EverywhereModRequireLicenseAcceptance>
                         </PropertyGroup>
                         <ItemGroup>
                           <ProjectReference Include="OtherCore/Other.Core.csproj" />
                           <Reference Include="ModFixture.B" HintPath="{Escape(extraAssembly)}" />
                           <EverywhereModLocalization Include="en" Name="Meta Fixture" Description="Fixture description" />
                           <EverywhereModDependency Include="other.core" Version="[1.0.0,2.0.0)" Optional="true" Assemblies="Other.Core;Other.Extra" />
                           <None Include="native/hook.dll" Pack="true" PackagePath="runtimes/win-x64/native/" />
                           <None Include="native/libhook.so.1" Pack="true" PackagePath="runtimes/linux-x64/native/" />
                           <None Include="native/custom_bridge.pyd" Pack="true" PackagePath="runtimes/osx-arm64/native/" />
                           <None Include="LICENSE" Pack="true" PackagePath="LICENSE" />
                           <None Include="Licenses/DependencyA.txt" Pack="true" PackagePath="Licenses/" />
                           <None Include="extra/manual.txt" Pack="true" PackagePath="Docs/manual.txt" />
                         </ItemGroup>
                         <Import Project="{Escape(Path.Combine(sdk, "Sdk", "Sdk.targets"))}" />
                       </Project>
                       """;
        await File.WriteAllTextAsync(Path.Combine(_root, "Mod.csproj"), project);

        var outputDirectory = Path.Combine(_root, "mod-output");
        var packResult = await RunAsync(
            _root,
            "msbuild",
            "Mod.csproj",
            "-restore",
            "-target:EverywherePack",
            "-nologo",
            "-verbosity:minimal",
            "-property:Version=1.0.0",
            "-property:OutputPath=" + outputDirectory);
        Assert.That(packResult.ExitCode, Is.Zero, packResult.Output);

        var packageZip = Path.Combine(outputDirectory, "fixture.meta-1.0.0.zip");
        var unpacked = Path.Combine(_root, "unpacked");
        await ZipFile.ExtractToDirectoryAsync(packageZip, unpacked);
        var manifest = ModManifestFile.Read(unpacked);

        Assert.Multiple(() =>
        {
            Assert.That(manifest.Author, Is.EqualTo("O'Connor; Sons"));
            Assert.That(manifest.Icon, Is.EqualTo("Assets/icon.svg"));
            Assert.That(manifest.Homepage, Is.EqualTo("https://example.com/fixture"));
            Assert.That(manifest.Repository?.Url, Is.EqualTo("https://github.com/example/fixture?ref=main"));
            Assert.That(manifest.Repository?.Directory, Is.EqualTo("mods/fixture"));
            Assert.That(manifest.License?.Spdx, Is.EqualTo("LicenseRef-TestProprietary"));
            Assert.That(manifest.License?.File, Is.EqualTo("LICENSE"));
            Assert.That(manifest.License?.IsAcceptanceRequired, Is.True);
            Assert.That(manifest.License?.ThirdPartyFiles, Is.EqualTo(["Licenses/DependencyA.txt"]));

            Assert.That(manifest.Entry, Is.TypeOf<UniversalModEntry>());
            var universal = (UniversalModEntry)manifest.Entry;
            Assert.That(universal.Assemblies, Does.Contain("ModFixture.B.dll"));
            Assert.That(universal.Assemblies, Does.Not.Contain("Other.Core.dll"));
            Assert.That(File.Exists(Path.Combine(unpacked, "Other.Core.dll")), Is.False);
            Assert.That(File.Exists(Path.Combine(unpacked, "Other.Core.pdb")), Is.False);
            Assert.That(File.Exists(Path.Combine(unpacked, "Other.Core.xml")), Is.False);

            Assert.That(File.Exists(Path.Combine(unpacked, "runtimes", "win-x64", "native", "hook.dll")), Is.True);
            Assert.That(File.Exists(Path.Combine(unpacked, "runtimes", "linux-x64", "native", "libhook.so.1")), Is.True);
            Assert.That(File.Exists(Path.Combine(unpacked, "runtimes", "osx-arm64", "native", "custom_bridge.pyd")), Is.True);
            Assert.That(File.Exists(Path.Combine(unpacked, "Docs", "manual.txt")), Is.True);

            var dependency = universal.Dependencies.Single();
            Assert.That(dependency.Id, Is.EqualTo("other.core"));
            Assert.That(dependency.Version, Is.EqualTo("[1.0.0,2.0.0)"));
            Assert.That(dependency.IsOptional, Is.True);
            Assert.That(dependency.Assemblies, Is.EqualTo(["Other.Core", "Other.Extra"]));
        });

        var keysDir = Path.Combine(_root, "keys");
        var privateKeyPath = Path.Combine(keysDir, "signing.pem");
        var publicKeyPath = Path.Combine(keysDir, "signing.pub");
        var keygenResult = await RunAsync(_root, tools, "keygen", privateKeyPath, publicKeyPath);
        Assert.That(keygenResult.ExitCode, Is.Zero, keygenResult.Output);

        var siteDir = Path.Combine(_root, "site");
        var indexPath = Path.Combine(siteDir, "index.json");
        var specPath = Path.Combine(_root, "index-spec.json");
        var specJson = $$"""
                         {
                           // Index build specification supports comments and trailing commas.
                           "format": 1,
                           "id": "fixture.repo",
                           "title": { "en": "Fixture Repo" },
                           "mirrors": ["https://gitee.com/example/fixture-mods/releases/download/index/index.json"],
                           "metadata": {
                             "categories": {
                               "utilities": { "title": { "en": "Utilities" } }
                             }
                           },
                           "layout": "{{layout}}",
                           "packages": [
                             {
                               "path": {{JsonSerializer.Serialize(packageZip)}},
                               "category": "utilities",
                               "keywords": ["sample"],
                               "isDefaultEnabled": true,
                               "channels": ["stable"],
                             }
                           ]
                         }
                         """;
        await File.WriteAllTextAsync(specPath, specJson);

        var buildIndexResult = await RunAsync(_root, tools, "build-index", specPath, indexPath, privateKeyPath);
        Assert.That(buildIndexResult.ExitCode, Is.Zero, buildIndexResult.Output);

        var index = ModIndexFile.Read(indexPath);
        var entry = index.Mods.Single();
        var version = entry.Versions.Single();
        var expectedPublicKey = await File.ReadAllTextAsync(publicKeyPath);

        Assert.Multiple(() =>
        {
            Assert.That(index.PublicKey, Is.EqualTo(expectedPublicKey));
            Assert.That(index.Signature, Does.StartWith(ModSignature.AlgorithmPrefix));
            Assert.That(ModSignature.VerifyIndex(index, expectedPublicKey), Is.True);
            Assert.That(entry.Id, Is.EqualTo("fixture.meta"));
            Assert.That(entry.Author, Is.EqualTo("O'Connor; Sons"));
            Assert.That(entry.License?.IsAcceptanceRequired, Is.True);
            Assert.That(entry.Icon, Does.StartWith("data:image/svg+xml;base64,"));
            Assert.That(entry.Channels["stable"], Is.EqualTo("1.0.0"));
            if (layout == "directory")
            {
                Assert.That(version.Artifact.Url, Is.EqualTo("packages/fixture.meta/1.0.0/fixture.meta-1.0.0.zip"));
                Assert.That(File.Exists(Path.Combine(siteDir, "packages", "fixture.meta", "1.0.0", "fixture.meta-1.0.0.zip")), Is.True);
                Assert.That(version.LicenseUrl, Is.EqualTo("details/fixture.meta/1.0.0/LICENSE"));
                Assert.That(version.ThirdPartyLicenseUrls, Is.EqualTo(["details/fixture.meta/1.0.0/Licenses/DependencyA.txt"]));
                Assert.That(version.Readme["en"], Is.EqualTo("details/fixture.meta/1.0.0/README.md"));
            }
            else
            {
                Assert.That(version.Artifact.Url, Is.EqualTo("../fixture.meta-1.0.0/fixture.meta-1.0.0.zip"));
                Assert.That(version.LicenseUrl, Is.Null);
            }
        });
    }

    /// <summary>
    /// RID builds normalize NuGet NativeCopyLocalItems into runtimes/{rid}/native/, while universal builds reject RID-specific managed DLLs.
    /// </summary>
    [Test]
    public async Task Pack_NativeCopyLocalAndRuntimeTargets_EnforcesNuGetRuntimeLayout()
    {
        var hostProject = Path.Combine(_repository, "tests", "Everywhere.Modding.Tests", "Fixtures", "Host", "ModFixture.Host.csproj");
        var sdk = Path.Combine(_repository, "src", "Everywhere.Modding.Sdk");
        var toolsDir = Path.Combine(sdk, "bin", _configuration, "net11.0");
        var exported = await RunAsync(
            _repository,
            "msbuild",
            hostProject,
            "-target:EverywhereExportModHost",
            "-property:Configuration=" + _configuration,
            "-getTargetResult:EverywhereExportModHost",
            "-nologo",
            "-verbosity:quiet");
        Assert.That(exported.ExitCode, Is.Zero, exported.Output);
        using var export = JsonDocument.Parse(exported.Output);
        var descriptor = export.RootElement.GetProperty("TargetResults")
            .GetProperty("EverywhereExportModHost")
            .GetProperty("Items")[0]
            .GetProperty("Identity")
            .GetString();
        var hostDirectory = Path.GetDirectoryName(descriptor) ?? throw new AssertionException("Missing host export directory.");

        Directory.CreateDirectory(Path.Combine(_root, "native"));
        await File.WriteAllTextAsync(Path.Combine(_root, "native", "nuget_native.dll"), "native-payload");
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Entry.cs"),
            """
            using Everywhere.Modding.Abstractions;
            namespace RidLayoutFixture;
            public sealed class Entry : IMod
            {
                public ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
            }
            """);

        var ridProject = $"""
                          <Project>
                            <Import Project="{Escape(Path.Combine(sdk, "Sdk", "Sdk.props"))}" />
                            <PropertyGroup>
                              <ImplicitUsings>enable</ImplicitUsings>
                              <Nullable>enable</Nullable>
                              <RuntimeIdentifier>win-x64</RuntimeIdentifier>
                              <EverywhereHost>{Escape(hostDirectory)}</EverywhereHost>
                              <EverywhereModToolsDirectory>{Escape(toolsDir)}</EverywhereModToolsDirectory>
                              <EverywhereModId>fixture.rid</EverywhereModId>
                              <EverywhereModPlatform>win-x64</EverywhereModPlatform>
                              <EverywhereModEntryType>RidLayoutFixture.Entry</EverywhereModEntryType>
                            </PropertyGroup>
                            <Import Project="{Escape(Path.Combine(sdk, "Sdk", "Sdk.targets"))}" />
                            <Target Name="InjectNuGetNative" AfterTargets="Build" BeforeTargets="EverywherePack">
                              <ItemGroup>
                                <NativeCopyLocalItems Include="$(MSBuildProjectDirectory)/native/nuget_native.dll" CopyLocal="true" AssetType="native" />
                                <ReferenceCopyLocalPaths Include="$(MSBuildProjectDirectory)/native/nuget_native.dll" AssetType="native" />
                              </ItemGroup>
                            </Target>
                          </Project>
                          """;
        await File.WriteAllTextAsync(Path.Combine(_root, "Mod.csproj"), ridProject);

        var ridOutput = Path.Combine(_root, "rid-out");
        var ridResult = await RunAsync(
            _root,
            "msbuild",
            "Mod.csproj",
            "-restore",
            "-target:EverywherePack",
            "-nologo",
            "-verbosity:minimal",
            "-property:Version=1.0.0",
            "-property:OutputPath=" + ridOutput);
        Assert.That(ridResult.ExitCode, Is.Zero, ridResult.Output);

        await using (var archive = await ZipFile.OpenReadAsync(Path.Combine(ridOutput, "fixture.rid-1.0.0.zip")))
        {
            Assert.That(archive.Entries.Select(e => e.FullName), Does.Contain("runtimes/win-x64/native/nuget_native.dll"));
        }

        // Direct CLI check: platform-specific managed runtime targets require EverywhereModPlatform.
        var packDir = Path.Combine(_root, "managed-rid-pack");
        Directory.CreateDirectory(packDir);
        var assembly = Path.Combine(AppContext.BaseDirectory, "fixtures", "ModFixture.B.dll");
        var contract = typeof(IMod).Assembly.Location;
        var refs = Path.Combine(_root, "refs.txt");
        await File.WriteAllLinesAsync(refs, [contract]);
        await File.WriteAllLinesAsync(refs + ".runtime.txt", [contract]);
        await File.WriteAllTextAsync(
            refs + ".host.json",
            JsonSerializer.Serialize(
                new ModHostDescriptor
                {
                    HostVersion = "1.0.0",
                    ModApiVersion = "1.0.0",
                    Framework = "net11.0",
                    RuntimeIdentifier = "win-x64",
                    EntryAssembly = "Host.dll"
                }));
        await File.WriteAllTextAsync(
            Path.Combine(packDir, "payload.txt"),
            $"{assembly}|runtimes/win-x64/lib/net11.0/ModFixture.B.dll|runtime||");
        await WritePackPropertiesAsync(
            Path.Combine(packDir, "properties.txt"),
            assembly,
            refs,
            "fixture.universal",
            "1.0.0",
            "ModFixture.Entry",
            "",
            Path.Combine(_root, "universal-invalid.zip"));

        var tools = Path.Combine(toolsDir, "Everywhere.Modding.Sdk.dll");
        var invalidResult = await RunAsync(_root, tools, "pack", packDir);
        Assert.That(invalidResult.ExitCode, Is.Not.Zero);
        Assert.That(invalidResult.Output, Does.Contain("PlatformRequiredForManagedRuntimeTargets"));
    }

    private Task WritePackPropertiesAsync(
        string path,
        string assemblyPath,
        string referenceList,
        string modId,
        string version,
        string entryType,
        string hostRange,
        string outputPath
    )
    {
        return File.WriteAllLinesAsync(
            path,
            [
                $"AssemblyPath={assemblyPath}",
                $"ReferenceList={referenceList}",
                $"ModId={modId}",
                $"Version={version}",
                $"EntryType={entryType}",
                "Framework=net11.0",
                $"HostRange={hostRange}",
                "ApiRange=",
                "Platform=universal",
                $"OutputPath={outputPath}",
                $"ProjectDirectory={_root}"
            ]);
    }

    /// <summary>
    /// Escapes fixture project values as XML data, including paths containing ampersands.
    /// </summary>
    private static string Escape(string value) => SecurityElement.Escape(value);

    /// <summary>
    /// Runs the normal SDK process entrypoint without shell argument interpolation.
    /// </summary>
    private static async Task<ProcessResult> RunAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new AssertionException("Could not start dotnet.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new ProcessResult { ExitCode = process.ExitCode, Output = await outputTask + await errorTask };
    }

    /// <summary>
    /// Captures process output after the child has released its files.
    /// </summary>
    private sealed record ProcessResult
    {
        /// <summary>
        /// Gets the process exit status.
        /// </summary>
        public required int ExitCode { get; init; }

        /// <summary>
        /// Gets standard output followed by standard error.
        /// </summary>
        public required string Output { get; init; }
    }
}
