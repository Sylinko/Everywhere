using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using System.Text.Json;
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
    /// Both host inputs use the same metadata path and must exclude the host's contract and implementation DLLs.
    /// </summary>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task Pack_HostImport_ExcludesHostAssemblies(bool isSourceImport, bool hasImplementationReference)
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
                         <Import Project="{Escape(Path.Combine(sdk, "Sdk", "Sdk.targets"))}" />
                       </Project>
                       """;
        await File.WriteAllTextAsync(Path.Combine(_root, "Plugin.csproj"), project);
        await File.WriteAllTextAsync(Path.Combine(_root, "Strings.resx"), "<root><data name=\"Greeting\"><value>Hello</value></data></root>");
        await File.WriteAllTextAsync(Path.Combine(_root, "Strings.fr.resx"), "<root><data name=\"Greeting\"><value>Bonjour</value></data></root>");
        var body = hasImplementationReference ? "GC.KeepAlive(PluginFixture.Host.HostApi.Name);" : "";
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
        var result = await RunAsync(_root, "msbuild", "Plugin.csproj", "-restore", "-target:EverywherePack", "-nologo", "-verbosity:minimal");
        Assert.That(result.ExitCode, Is.Zero, result.Output);
        var packageResult = result.Output.Split('\n').Select(line => line.Trim())
            .Single(line => line.StartsWith("{\"code\":\"PackageCreated\"", StringComparison.Ordinal));
        using var packageDocument = JsonDocument.Parse(packageResult);
        Assert.That(packageDocument.RootElement.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "code", "path" }));
        Assert.That(packageDocument.RootElement.GetProperty("path").GetString(),
            Is.EqualTo(Path.Combine(_root, "bin", "Debug", "net10.0", "fixture.sdk-1.0.0.zip")));
        using var archive = ZipFile.OpenRead(Path.Combine(_root, "bin", "Debug", "net10.0", "fixture.sdk-1.0.0.zip"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Contain("Plugin.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Contain("fr/Plugin.resources.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("Everywhere.Plugin.Abstractions.dll"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Does.Not.Contain("PluginFixture.Host.dll"));
        var manifestEntry = archive.GetEntry("plugin.json") ?? throw new AssertionException("No generated manifest.");
        using var stream = manifestEntry.Open();
        using var manifest = JsonDocument.Parse(stream);
        var entry = manifest.RootElement.GetProperty("entry");
        Assert.That(entry.GetProperty("$type").GetString(), Is.EqualTo("universal"));
        Assert.That(entry.GetProperty("pluginApiVersion").GetString(), Is.EqualTo("[1.0.0,2.0.0)"));
        Assert.That(entry.GetProperty("hostVersion").GetString(), Is.EqualTo(hasImplementationReference ? "[0.9.0,0.10.0)" : null));
        Assert.That(File.Exists(Path.Combine(hostDirectory, "PluginFixture.Host.xml")), Is.True);
    }

    /// <summary>
    /// Author tooling receives the same success and error fields from the source-generated command DTOs.
    /// </summary>
    [Test]
    public async Task Check_ValidAndInvalidManifest_PreservesCommandResults()
    {
        var sdk = Path.Combine(_repository, "src", "Everywhere.Plugin.Sdk", "bin", _configuration, "net10.0", "Everywhere.Plugin.Sdk.dll");
        var manifest = new PluginManifest
        {
            Id = "fixture.sdk",
            Version = "1.0.0",
            Entry = new UniversalPluginEntry
            {
                Assembly = "Entry.dll",
                Type = "Fixture.Entry",
                Framework = "net10.0",
                PluginApiVersion = "[1.0,2.0)",
                Assemblies = ["Entry.dll"]
            }
        };
        PluginManifestFile.Write(_root, manifest);
        var valid = await RunAsync(_root, sdk, "check", _root);
        Assert.That(valid.ExitCode, Is.Zero, valid.Output);
        using var success = JsonDocument.Parse(valid.Output);
        Assert.That(success.RootElement.GetProperty("code").GetString(), Is.EqualTo("ManifestValid"));
        Assert.That(success.RootElement.GetProperty("plugin").GetString(), Is.EqualTo(manifest.Id));
        Assert.That(success.RootElement.GetProperty("version").GetString(), Is.EqualTo(manifest.Version));

        var path = Path.Combine(_root, PluginManifestFile.FileName);
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("fixture.sdk", "Invalid Plugin ID"));
        var invalid = await RunAsync(_root, sdk, "check", _root);
        Assert.That(invalid.ExitCode, Is.EqualTo(1));
        using var error = JsonDocument.Parse(invalid.Output);
        Assert.That(error.RootElement.GetProperty("code").GetString(), Is.EqualTo("InvalidManifest"));
        Assert.That(error.RootElement.GetProperty("message").GetString(), Is.Not.Empty);
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