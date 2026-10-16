using System.Text.Json.Nodes;
using Everywhere.Plugin.Abstractions;
using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Tests;

/// <summary>
/// Checks host import boundaries against dependency files without loading their declared assemblies.
/// </summary>
public sealed class HostReferenceSetTests
{
    private string _root = "";

    /// <summary>
    /// Creates an isolated distribution with a minimal, plugin-enabled host descriptor.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "Everywhere.HostReferenceSet.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, HostReferenceSet.DescriptorFileName),
            """
            {
              "SCHEMAVERSION": "1",
              "hostVersion": "0.9.0",
              "pluginApiVersion": "1.0.0",
              "framework": "net10.0",
              "runtimeIdentifier": "win-x64",
              "entryAssembly": "Host.dll",
              "futureHostField": true
            }
            """);
        File.WriteAllText(
            Path.Combine(_root, "Host.deps.json"),
            """
            {
              "runtimeTarget": { "name": "net10.0/win-x64", "signature": "ignored" },
              "targets": {
                "net10.0": { "CompileOnly/1.0.0": { "runtime": { "NotSelected.dll": {} } } },
                "net10.0/win-x64": {
                  "Everywhere.Plugin.Abstractions/1.0.0": {
                    "runtime": {
                      "lib/net10.0/Everywhere.Plugin.Abstractions.dll": { "assemblyVersion": "1.0.0.0" },
                      "fr/Everywhere.Plugin.Abstractions.resources.dll": {},
                      "ignored.pdb": null
                    },
                    "dependencies": { "Unused": "1.0.0" }
                  },
                  "RuntimePack/10.0.0": { "runtime": { "System.Private.CoreLib.dll": {} } },
                  "NativeOnly/1.0.0": { "native": { "library.dll": {} } }
                }
              },
              "libraries": {
                "Everywhere.Plugin.Abstractions/1.0.0": { "type": "project" },
                "RuntimePack/10.0.0": { "type": "runtimepack" }
              },
              "compilationOptions": {}
            }
            """);
        File.Copy(typeof(IPlugin).Assembly.Location, Path.Combine(_root, "Everywhere.Plugin.Abstractions.dll"));
        File.Copy(typeof(object).Assembly.Location, Path.Combine(_root, "System.Private.CoreLib.dll"));
    }

    /// <summary>
    /// Removes only the distribution owned by this test.
    /// </summary>
    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Target selection, runtime-pack exclusion and declared-path fallback must survive the JSON refactor.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void Import_DeclaredAssets_UsesOnlySelectedManagedReferences(bool hasRelativeAsset)
    {
        var expected = Path.Combine(_root, "Everywhere.Plugin.Abstractions.dll");

        if (hasRelativeAsset)
        {
            var directory = Path.Combine(_root, "lib", "net10.0");
            Directory.CreateDirectory(directory);
            var relative = Path.Combine(directory, "Everywhere.Plugin.Abstractions.dll");
            File.Copy(expected, relative);
            expected = relative;
        }

        File.WriteAllText(Path.Combine(_root, "Undeclared.dll"), "This file must never be inspected.");
        var references = new HostReferenceSet(_root);

        Assert.That(references.Descriptor.SchemaVersion, Is.EqualTo(1));
        Assert.That(references.Assemblies, Has.Count.EqualTo(1));
        Assert.That(references.Assemblies["Everywhere.Plugin.Abstractions"], Is.EqualTo(expected));
        Assert.That(references.RuntimeAssemblies, Has.Count.EqualTo(2));
        Assert.That(references.RuntimeAssemblies["System.Private.CoreLib"],
            Is.EqualTo(Path.Combine(_root, "System.Private.CoreLib.dll")));
    }

    /// <summary>
    /// Explicitly missing selection and asset failures retain their existing importer diagnostics.
    /// </summary>
    [TestCase("nullTarget", "InvalidHostDependencies")]
    [TestCase("missingAsset", "MissingHostAsset")]
    [TestCase("duplicateIdentity", "AmbiguousHostAsset")]
    [TestCase("missingContract", "MissingPluginContract")]
    [TestCase("escapingPath", "InvalidPackagePath")]
    public void Import_InvalidSelection_PreservesValidationCode(string scenario, string expectedCode)
    {
        var path = Path.Combine(_root, "Host.deps.json");
        var document = ReadObject(path);
        var target = document["targets"]?["net10.0/win-x64"]?.AsObject()
            ?? throw new AssertionException("Missing fixture target.");
        var library = target["Everywhere.Plugin.Abstractions/1.0.0"]?.AsObject()
            ?? throw new AssertionException("Missing fixture library.");
        var runtime = library["runtime"]?.AsObject() ?? throw new AssertionException("Missing fixture assets.");

        switch (scenario)
        {
            case "nullTarget":
                document["runtimeTarget"] = new JsonObject { ["name"] = null };
                break;
            case "missingAsset":
                runtime["Missing.dll"] = new JsonObject();
                break;
            case "duplicateIdentity":
                File.Copy(typeof(IPlugin).Assembly.Location, Path.Combine(_root, "Duplicate.dll"));
                runtime["Duplicate.dll"] = new JsonObject();
                break;
            case "missingContract":
                library.Remove("runtime");
                break;
            case "escapingPath":
                runtime["../Outside.dll"] = new JsonObject();
                break;
        }

        File.WriteAllText(path, document.ToJsonString());
        var exception = Assert.Throws<PluginValidationException>(() => new HostReferenceSet(_root));
        Assert.That(exception?.Code, Is.EqualTo(expectedCode));
    }

    /// <summary>
    /// Reads mutable fixture data without sharing the production serialization context.
    /// </summary>
    private static JsonObject ReadObject(string path)
    {
        return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new AssertionException("Missing fixture document.");
    }
}