using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using System.Text.Json;
using System.Xml.Linq;
using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Installation;
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
    /// Both compilation paths preserve mod resources and exclude host assemblies and content.
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
        var tools = Path.Combine(sdk, "bin", _configuration, "net10.0");
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
        var descriptor = export.RootElement.GetProperty("TargetResults").GetProperty("EverywhereExportModHost")
            .GetProperty("Items")[0].GetProperty("Identity").GetString();
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
                           <Content Include="shared/中文指南.md" Link="docs/中文指南.md" CopyToOutputDirectory="PreserveNewest" />
                           <Content Include="docs/images/example.txt" CopyToOutputDirectory="PreserveNewest" />
                           <Content Include="docs/sample.dll" CopyToOutputDirectory="PreserveNewest" />
                           <Content Include="{Escape(Path.Combine(hostDirectory, "fr", "ModFixture.Host.resources.dll"))}"
                                    Link="fr/ModFixture.Host.resources.dll" CopyToOutputDirectory="PreserveNewest" AssetType="resources" />
                           <Reference Include="ModFixture.B" HintPath="{Escape(Path.Combine(_repository, "tests", "Everywhere.Modding.Tests", "Fixtures", "B", "bin", _configuration, "net10.0", "ModFixture.B.dll"))}" />
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
        await File.WriteAllTextAsync(Path.Combine(_root, "shared", "中文指南.md"), "# 中文\n![example](images/example.txt)");
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "images", "example.txt"), "attachment");
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "sample.dll"), "A documentation sample, not a loadable library.");
        await File.WriteAllTextAsync(Path.Combine(_root, "Strings.resx"), "<root><data name=\"Greeting\"><value>Hello</value></data></root>");
        await File.WriteAllTextAsync(Path.Combine(_root, "Strings.fr.resx"), "<root><data name=\"Greeting\"><value>Bonjour</value></data></root>");
        var body = hasImplementationReference ?
            "GC.KeepAlive(ModFixture.Host.HostApi.Name); GC.KeepAlive(NuGet.Versioning.NuGetVersion.Parse(\"1.0.0\"));" :
            "";
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
        var packageResult = result.Output.Split('\n').Select(line => line.Trim())
            .Single(line => line.StartsWith("{\"code\":\"PackageCreated\"", StringComparison.Ordinal));
        using var packageDocument = JsonDocument.Parse(packageResult);
        Assert.That(packageDocument.RootElement.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "code", "path" }));
        Assert.That(
            packageDocument.RootElement.GetProperty("path").GetString(),
            Is.EqualTo(Path.Combine(outputDirectory, "fixture.sdk-1.2.3.zip")));
        await using var archive = await ZipFile.OpenReadAsync(Path.Combine(outputDirectory, "fixture.sdk-1.2.3.zip"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Contain("Mod.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Contain("fr/Mod.resources.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Contain("fr/ModFixture.B.resources.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("fr/ModFixture.Host.resources.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Modding.Abstractions.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Versioning.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Modding.Packaging.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("ModFixture.Host.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("NuGet.Versioning.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("host-content.txt"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Modding.Abstractions.pdb"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Modding.Abstractions.xml"));
        using var importedHost = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(_root, "obj", "Debug", "net10.0", "everywhere-host-references.txt.host.json")));
        Assert.That(importedHost.RootElement.GetProperty("HostVersion").GetString(), Is.EqualTo("0.9.2"));
        Assert.That(File.Exists(Path.Combine(outputDirectory, "mod-host.json")), Is.False);
        var manifestEntry = archive.GetEntry("mod.json") ?? throw new AssertionException("No generated manifest.");
        await using var stream = await manifestEntry.OpenAsync();
        using var manifest = await JsonDocument.ParseAsync(stream);
        var entry = manifest.RootElement.GetProperty("entry");
        Assert.That(entry.GetProperty("$type").GetString(), Is.EqualTo("universal"));
        Assert.That(entry.GetProperty("contentFiles").EnumerateArray().Select(value => value.GetString()), Does.Contain("docs/sample.dll"));
        Assert.That(entry.GetProperty("nativeLibraries").GetArrayLength(), Is.Zero);
        Assert.That(
            entry.GetProperty("assemblies").EnumerateArray().Select(value => value.GetString()),
            Does.Contain("ModFixture.B.dll").And.Contain("fr/ModFixture.B.resources.dll").And.Not.Contain("docs/sample.dll"));
        Assert.That(entry.GetProperty("modApiVersion").GetString(), Is.EqualTo("[1.0.0,2.0.0-0)"));
        Assert.That(entry.GetProperty("hostVersion").GetString(), Is.EqualTo(hasImplementationReference ? "[0.9.0,0.10.0)" : null));
        Assert.That(File.Exists(Path.Combine(hostDirectory, "ModFixture.Host.xml")), Is.True);
        Assert.Multiple(() =>
        {
            var names = manifest.RootElement.GetProperty("name");
            Assert.That(names.EnumerateObject().Count(), Is.EqualTo(hasImplementationReference ? 1 : 0));
            if (hasImplementationReference) Assert.That(names.GetProperty("en").GetString(), Is.EqualTo("Tools & Search"));
            Assert.That(
                manifest.RootElement.GetProperty("description").GetProperty("en").GetString(),
                Is.EqualTo("A & B | C; \"quoted\" 'text' it's (fine)\nSecond line"));
            Assert.That(manifest.RootElement.GetProperty("readme").GetProperty("en").GetString(), Is.EqualTo("README.md"));
            Assert.That(manifest.RootElement.GetProperty("readme").GetProperty("zh-hans").GetString(), Is.EqualTo("docs/中文指南.md"));
            Assert.That(archive.GetEntry("README.md"), Is.Not.Null);
            Assert.That(archive.GetEntry("README.zh-Hans.md"), Is.Null);
            Assert.That(archive.GetEntry("docs/中文指南.md"), Is.Not.Null);
            Assert.That(archive.GetEntry("docs/images/example.txt"), Is.Not.Null);
        });

        var store = new ModPackageStore(Path.Combine(_root, "store"), Path.Combine(_root, "data"), Path.Combine(_root, "staging"));
        await using var packageStream = File.OpenRead(Path.Combine(outputDirectory, "fixture.sdk-1.2.3.zip"));
        var installed = await store.ImportAsync(packageStream);
        Assert.That(installed.Manifest.Description["ZH-HANS"], Is.EqualTo("搜索本地文件"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(installed.Directory, installed.Manifest.Readme["EN"])), Is.EqualTo("# English"));

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

            // A project with no localization declarations still packages its conventional READMEs.
            File.Delete(Path.Combine(_root, "README.en.md"));
            var plainProject = XDocument.Parse(project);
            plainProject.Descendants("EverywhereModLocalization").Remove();
            await File.WriteAllTextAsync(Path.Combine(_root, "Mod.csproj"), plainProject.ToString());
            var plain = await RunAsync(_root, "msbuild", "Mod.csproj", "-target:EverywherePack", "-nologo", "-verbosity:minimal");
            Assert.That(plain.ExitCode, Is.Zero, plain.Output);
            var defaultArchive = Path.Combine(_root, "bin", "Debug", "net10.0", "fixture.sdk-1.0.0.zip");
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

            // Link can rename the same source file inside the package without creating an English translation.
            plainProject.Descendants("EverywhereModLocalization").Single().SetAttributeValue("Readme", "docs/guide.md");
            plainProject.Root?.Add(
                new XElement(
                    "ItemGroup",
                    new XElement(
                        "Content",
                        new XAttribute("Include", "README.md"),
                        new XAttribute("Link", "docs/guide.md"),
                        new XAttribute("CopyToOutputDirectory", "PreserveNewest"))));
            await File.WriteAllTextAsync(Path.Combine(_root, "Mod.csproj"), plainProject.ToString());
            var linked = await RunAsync(_root, "msbuild", "Mod.csproj", "-target:EverywherePack", "-nologo", "-verbosity:minimal");
            Assert.That(linked.ExitCode, Is.Zero, linked.Output);
            var linkedDirectory = Path.Combine(_root, "linked");
            await ZipFile.ExtractToDirectoryAsync(defaultArchive, linkedDirectory);
            var linkedManifest = ModManifestFile.Read(linkedDirectory);
            Assert.That(linkedManifest.Readme.Keys, Is.EquivalentTo(new[] { "zh-hans" }));
            Assert.That(linkedManifest.Readme["zh-hans"], Is.EqualTo("docs/guide.md"));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(linkedDirectory, "docs", "guide.md")), Is.EqualTo("# English"));

            // A damaged compiler output must fail as managed input, not become a native library.
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
    /// A real SDK command and runtime definition agree on prerelease host ranges and exact API defaults.
    /// </summary>
    [Test]
    public async Task Pack_PrereleaseHostAndContract_AgreesWithRuntimeCompatibility()
    {
        var tools = Path.Combine(_repository, "src", "Everywhere.Modding.Sdk", "bin", _configuration, "net10.0", "Everywhere.Modding.Sdk.dll");
        var references = Path.Combine(_root, "references.txt");
        var payload = Path.Combine(_root, "payload.txt");
        var presentation = Path.Combine(_root, "presentation");
        Directory.CreateDirectory(presentation);
        await File.WriteAllTextAsync(Path.Combine(presentation, "localizations.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(presentation, "readmes.txt"), "");
        var archivePath = Path.Combine(_root, "mod.zip");
        var assembly = Path.Combine(AppContext.BaseDirectory, "fixtures", "ModFixture.B.dll");
        var contract = typeof(IMod).Assembly.Location;
        var host = new ModHostDescriptor
        {
            HostVersion = "0.9.0-canary.20261016.10",
            ModApiVersion = "1.1.0-preview.10",
            Framework = "net10.0",
            RuntimeIdentifier = "win-x64",
            EntryAssembly = "Host.dll"
        };
        await File.WriteAllLinesAsync(references, [contract]);
        await File.WriteAllLinesAsync(references + ".runtime.txt", [contract]);
        await File.WriteAllTextAsync(references + ".host.json", JsonSerializer.Serialize(host));
        await File.WriteAllTextAsync(payload, string.Empty);

        var result = await RunAsync(
            _root,
            tools,
            "pack",
            assembly,
            references,
            payload,
            "fixture.preview",
            "1.0.0-preview.2+commit",
            "ModFixture.Entry",
            "net10.0",
            "[0.9.0-canary.20261016.2,0.9.0)",
            "",
            "universal",
            archivePath,
            presentation,
            _root);
        Assert.That(result.ExitCode, Is.Zero, result.Output);

        var directory = Path.Combine(_root, "unpacked");
        await ZipFile.ExtractToDirectoryAsync(archivePath, directory);
        var installation = new ModInstallation
        {
            InstallationId = "preview",
            Directory = directory,
            Manifest = ModManifestFile.Read(directory)
        };
        var definition = new ModDefinition(
            installation,
            Path.Combine(_root, "data"),
            host.RuntimeIdentifier,
            host.HostVersion,
            host.ModApiVersion,
            host.Framework);
        Assert.Multiple(() =>
        {
            Assert.That(definition.Diagnostic, Is.Null);
            Assert.That(definition.Entry.ModApiVersion, Is.EqualTo("[1.1.0-preview.10]"));
            Assert.That(installation.Manifest.Version, Is.EqualTo("1.0.0-preview.2+commit"));
            Assert.That(
                new ModDefinition(
                    installation,
                    Path.Combine(_root, "data"),
                    host.RuntimeIdentifier,
                    host.HostVersion,
                    "1.1.0-preview.11",
                    host.Framework).Diagnostic?.Code,
                Is.EqualTo("IncompatibleHost"));
        });

        var invalid = await RunAsync(
            _root,
            tools,
            "pack",
            assembly,
            references,
            payload,
            "fixture.preview",
            "1.0.0-preview.2",
            "ModFixture.Entry",
            "net10.0",
            "[0.9.0,0.10.0)",
            "",
            "universal",
            Path.Combine(_root, "invalid.zip"),
            presentation,
            _root);
        Assert.That(invalid.ExitCode, Is.Not.Zero);
        Assert.That(invalid.Output, Does.Contain("BuildHostOutsideRange"));
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