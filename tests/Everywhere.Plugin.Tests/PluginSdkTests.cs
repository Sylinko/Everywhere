using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using System.Text.Json;
using System.Xml.Linq;
using Everywhere.Plugin.Hosting.Installation;
using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Tests;

/// <summary>
/// Builds real author projects through SDK imports and checks their generated distribution artifacts.
/// </summary>
public sealed class PluginSdkTests
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
        _root = Path.Combine(Path.GetTempPath(), "Everywhere.Plugin.Sdk.Tests", Guid.NewGuid().ToString("N"));
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
    /// Both compilation paths preserve plugin resources and exclude host assemblies and content.
    /// Source references must also keep command-line plugin identity and output paths out of the host build.
    /// </summary>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task Pack_HostImport_PreservesPresentationAndExcludesHostFiles(bool isSourceImport, bool hasImplementationReference)
    {
        var hostProject = Path.Combine(_repository, "tests", "Everywhere.Plugin.Tests", "Fixtures", "Host", "PluginFixture.Host.csproj");
        var sdk = Path.Combine(_repository, "src", "Everywhere.Plugin.Sdk");
        var tools = Path.Combine(sdk, "bin", _configuration, "net10.0");
        var exported = await RunAsync(
            _repository,
            "msbuild",
            hostProject,
            "-target:EverywhereExportPluginHost",
            "-property:Configuration=" + _configuration,
            "-getTargetResult:EverywhereExportPluginHost",
            "-nologo",
            "-verbosity:quiet");
        Assert.That(exported.ExitCode, Is.Zero, exported.Output);
        using var export = JsonDocument.Parse(exported.Output);
        var descriptor = export.RootElement.GetProperty("TargetResults").GetProperty("EverywhereExportPluginHost")
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
                           <EverywherePluginToolsDirectory>{Escape(tools)}</EverywherePluginToolsDirectory>
                           <EverywherePluginId>fixture.sdk</EverywherePluginId>
                           <EverywherePluginEntryType>SdkFixture.Entry</EverywherePluginEntryType>
                           <EverywhereHostVersion>{(hasImplementationReference ? "[0.9.0,0.10.0)" : "")}</EverywhereHostVersion>
                         </PropertyGroup>
                         <ItemGroup>
                           <EverywherePluginLocalization Include="EN" Name="{(hasImplementationReference ? "Tools &amp; Search" : "")}" Description="A &amp; B | C; &quot;quoted&quot; &apos;text&apos; it&apos;s (fine)&#10;Second line" />
                           <EverywherePluginLocalization Include="zh-Hans" Description="搜索本地文件" Readme="docs/中文指南.md" />
                           <Content Include="shared/中文指南.md" Link="docs/中文指南.md" CopyToOutputDirectory="PreserveNewest" />
                           <Content Include="docs/images/example.txt" CopyToOutputDirectory="PreserveNewest" />
                         </ItemGroup>
                         <Import Project="{Escape(Path.Combine(sdk, "Sdk", "Sdk.targets"))}" />
                       </Project>
                       """;
        await File.WriteAllTextAsync(Path.Combine(_root, "Plugin.csproj"), project);
        Directory.CreateDirectory(Path.Combine(_root, "docs", "images"));
        Directory.CreateDirectory(Path.Combine(_root, "shared"));
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "# English");
        await File.WriteAllTextAsync(Path.Combine(_root, "README.zh-Hans.md"), "# Conventional Chinese");
        await File.WriteAllTextAsync(Path.Combine(_root, "shared", "中文指南.md"), "# 中文\n![example](images/example.txt)");
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "images", "example.txt"), "attachment");
        await File.WriteAllTextAsync(Path.Combine(_root, "Strings.resx"), "<root><data name=\"Greeting\"><value>Hello</value></data></root>");
        await File.WriteAllTextAsync(Path.Combine(_root, "Strings.fr.resx"), "<root><data name=\"Greeting\"><value>Bonjour</value></data></root>");
        var body = hasImplementationReference ?
            "GC.KeepAlive(PluginFixture.Host.HostApi.Name); GC.KeepAlive(NuGet.Versioning.NuGetVersion.Parse(\"1.0.0\"));" : "";
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Entry.cs"),
            $$"""
              using Everywhere.Plugin.Abstractions;
              namespace SdkFixture;
              public sealed class Entry : IPlugin
              {
                  public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
                  {
                      {{body}}
                      return ValueTask.CompletedTask;
                  }
              }
              """);
        var outputDirectory = Path.Combine(_root, "plugin-output");
        var result = await RunAsync(
            _root, "msbuild", "Plugin.csproj", "-restore", "-target:EverywherePack", "-nologo", "-verbosity:minimal",
            "-property:Version=1.2.3", "-property:OutputPath=" + outputDirectory,
            "-property:EverywhereHost=" + host);
        Assert.That(result.ExitCode, Is.Zero, result.Output);
        var packageResult = result.Output.Split('\n').Select(line => line.Trim())
            .Single(line => line.StartsWith("{\"code\":\"PackageCreated\"", StringComparison.Ordinal));
        using var packageDocument = JsonDocument.Parse(packageResult);
        Assert.That(packageDocument.RootElement.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "code", "path" }));
        Assert.That(packageDocument.RootElement.GetProperty("path").GetString(),
            Is.EqualTo(Path.Combine(outputDirectory, "fixture.sdk-1.2.3.zip")));
        using var archive = ZipFile.OpenRead(Path.Combine(outputDirectory, "fixture.sdk-1.2.3.zip"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Contain("Plugin.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Contain("fr/Plugin.resources.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Plugin.Abstractions.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Versioning.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Plugin.Packaging.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("PluginFixture.Host.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("NuGet.Versioning.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("host-content.txt"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Plugin.Abstractions.pdb"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Plugin.Abstractions.xml"));
        using var importedHost = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(_root, "obj", "Debug", "net10.0", "everywhere-host-references.txt.host.json")));
        Assert.That(importedHost.RootElement.GetProperty("HostVersion").GetString(), Is.EqualTo("0.9.2"));
        Assert.That(File.Exists(Path.Combine(outputDirectory, "plugin-host.json")), Is.False);
        var manifestEntry = archive.GetEntry("plugin.json") ?? throw new AssertionException("No generated manifest.");
        using var stream = manifestEntry.Open();
        using var manifest = JsonDocument.Parse(stream);
        var entry = manifest.RootElement.GetProperty("entry");
        Assert.That(entry.GetProperty("$type").GetString(), Is.EqualTo("universal"));
        Assert.That(entry.GetProperty("pluginApiVersion").GetString(), Is.EqualTo("[1.0.0,2.0.0-0)"));
        Assert.That(entry.GetProperty("hostVersion").GetString(), Is.EqualTo(hasImplementationReference ? "[0.9.0,0.10.0)" : null));
        Assert.That(File.Exists(Path.Combine(hostDirectory, "PluginFixture.Host.xml")), Is.True);
        Assert.Multiple(() =>
        {
            var names = manifest.RootElement.GetProperty("name");
            Assert.That(names.EnumerateObject().Count(), Is.EqualTo(hasImplementationReference ? 1 : 0));
            if (hasImplementationReference) Assert.That(names.GetProperty("en").GetString(), Is.EqualTo("Tools & Search"));
            Assert.That(manifest.RootElement.GetProperty("description").GetProperty("en").GetString(),
                Is.EqualTo("A & B | C; \"quoted\" 'text' it's (fine)\nSecond line"));
            Assert.That(manifest.RootElement.GetProperty("readme").GetProperty("en").GetString(), Is.EqualTo("README.md"));
            Assert.That(manifest.RootElement.GetProperty("readme").GetProperty("zh-hans").GetString(), Is.EqualTo("docs/中文指南.md"));
            Assert.That(archive.GetEntry("README.md"), Is.Not.Null);
            Assert.That(archive.GetEntry("README.zh-Hans.md"), Is.Null);
            Assert.That(archive.GetEntry("docs/中文指南.md"), Is.Not.Null);
            Assert.That(archive.GetEntry("docs/images/example.txt"), Is.Not.Null);
        });

        var store = new PluginPackageStore(Path.Combine(_root, "store"), Path.Combine(_root, "data"), Path.Combine(_root, "staging"));
        using var packageStream = File.OpenRead(Path.Combine(outputDirectory, "fixture.sdk-1.2.3.zip"));
        var installed = await store.ImportAsync(packageStream);
        Assert.That(installed.Manifest.Description["ZH-HANS"], Is.EqualTo("搜索本地文件"));
        Assert.That(File.ReadAllText(Path.Combine(installed.Directory, installed.Manifest.Readme["EN"])), Is.EqualTo("# English"));

        if (!isSourceImport && !hasImplementationReference)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, "README.en.md"), "# Other English");
            var ambiguous = await RunAsync(_root, "msbuild", "Plugin.csproj", "-target:EverywherePack", "-nologo", "-verbosity:minimal");
            Assert.That(ambiguous.ExitCode, Is.Not.Zero);
            Assert.That(ambiguous.Output, Does.Contain("DuplicateTranslation"));

            await File.WriteAllTextAsync(Path.Combine(_root, "Plugin.csproj"), project.Replace(
                "Include=\"EN\" Name=", "Include=\"EN\" Readme=\"README.en.md\" Name=", StringComparison.Ordinal));
            var explicitReadme = await RunAsync(_root, "msbuild", "Plugin.csproj", "-target:EverywherePack", "-nologo", "-verbosity:minimal");
            Assert.That(explicitReadme.ExitCode, Is.Zero, explicitReadme.Output);

            // A project with no localization declarations still packages its conventional READMEs.
            File.Delete(Path.Combine(_root, "README.en.md"));
            var plainProject = XDocument.Parse(project);
            plainProject.Descendants("EverywherePluginLocalization").Remove();
            await File.WriteAllTextAsync(Path.Combine(_root, "Plugin.csproj"), plainProject.ToString());
            var plain = await RunAsync(_root, "msbuild", "Plugin.csproj", "-target:EverywherePack", "-nologo", "-verbosity:minimal");
            Assert.That(plain.ExitCode, Is.Zero, plain.Output);
            var defaultArchive = Path.Combine(_root, "bin", "Debug", "net10.0", "fixture.sdk-1.0.0.zip");
            var plainDirectory = Path.Combine(_root, "plain");
            ZipFile.ExtractToDirectory(defaultArchive, plainDirectory);
            var plainManifest = PluginManifestFile.Read(plainDirectory);
            Assert.That(plainManifest.Name, Is.Empty);
            Assert.That(plainManifest.Description, Is.Empty);
            Assert.That(plainManifest.Readme.Keys, Is.EquivalentTo(new[] { "en", "zh-hans" }));

            // Explicitly mapping README.md to Chinese must not also infer an English mapping.
            plainProject.Root?.Add(new XElement("ItemGroup",
                new XElement("EverywherePluginLocalization", new XAttribute("Include", "zh-Hans"), new XAttribute("Readme", "README.md"))));
            await File.WriteAllTextAsync(Path.Combine(_root, "Plugin.csproj"), plainProject.ToString());
            var remapped = await RunAsync(_root, "msbuild", "Plugin.csproj", "-target:EverywherePack", "-nologo", "-verbosity:minimal");
            Assert.That(remapped.ExitCode, Is.Zero, remapped.Output);
            var remappedDirectory = Path.Combine(_root, "remapped");
            ZipFile.ExtractToDirectory(defaultArchive, remappedDirectory);
            var remappedManifest = PluginManifestFile.Read(remappedDirectory);
            Assert.That(remappedManifest.Name, Is.Empty);
            Assert.That(remappedManifest.Readme.Keys, Is.EquivalentTo(new[] { "zh-hans" }));
            Assert.That(remappedManifest.Readme["zh-hans"], Is.EqualTo("README.md"));

            // Link can rename the same source file inside the package without creating an English translation.
            plainProject.Descendants("EverywherePluginLocalization").Single().SetAttributeValue("Readme", "docs/guide.md");
            plainProject.Root?.Add(new XElement("ItemGroup",
                new XElement("Content", new XAttribute("Include", "README.md"), new XAttribute("Link", "docs/guide.md"),
                    new XAttribute("CopyToOutputDirectory", "PreserveNewest"))));
            await File.WriteAllTextAsync(Path.Combine(_root, "Plugin.csproj"), plainProject.ToString());
            var linked = await RunAsync(_root, "msbuild", "Plugin.csproj", "-target:EverywherePack", "-nologo", "-verbosity:minimal");
            Assert.That(linked.ExitCode, Is.Zero, linked.Output);
            var linkedDirectory = Path.Combine(_root, "linked");
            ZipFile.ExtractToDirectory(defaultArchive, linkedDirectory);
            var linkedManifest = PluginManifestFile.Read(linkedDirectory);
            Assert.That(linkedManifest.Readme.Keys, Is.EquivalentTo(new[] { "zh-hans" }));
            Assert.That(linkedManifest.Readme["zh-hans"], Is.EqualTo("docs/guide.md"));
            Assert.That(File.ReadAllText(Path.Combine(linkedDirectory, "docs", "guide.md")), Is.EqualTo("# English"));
        }
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