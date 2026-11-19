using System.Text.Json.Nodes;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Tests;

/// <summary>
/// Verifies format-1 static repository index parsing, validation rules, and multi-channel asset URL resolution.
/// </summary>
public sealed class ModIndexFileTests
{
    private const string SampleSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private string _root = "";

    /// <summary>
    /// Creates an isolated workspace for temporary index files.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "Everywhere.ModIndex.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Cleans up the test directory.
    /// </summary>
    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Signed indexes round-trip universal and per-platform entries, skip future fields, and resolve GitHub/Gitee release paths.
    /// </summary>
    [Test]
    public void Read_SignedMultiChannelIndex_ValidatesAndResolvesAssets()
    {
        var (privateKeyPem, publicKey) = ModSignature.GenerateKeyPair();
        var indexPath = Path.Combine(_root, ModIndexFile.FileName);
        var inlineSvg = $"data:image/svg+xml;base64,{Convert.ToBase64String("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8)}";

        var unsigned = new ModIndex
        {
            Format = 1,
            Id = "everywhere.official",
            Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = "Official Repository",
                ["zh-hans"] = "官方扩展源"
            },
            GeneratedAt = 1790476800,
            PublicKey = publicKey,
            Mirrors =
            [
                "https://gitee.com/Sylinko/Everywhere-Mods/releases/download/index/index.json",
                "http://mirror.local/index.json"
            ],
            Metadata = new ModIndexMetadata
            {
                Categories = new Dictionary<string, ModIndexDeclaration>(StringComparer.Ordinal)
                {
                    ["search"] = new()
                    {
                        Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Search" }
                    }
                },
                Labels = new Dictionary<string, ModIndexDeclaration>(StringComparer.Ordinal)
                {
                    ["verified"] = new()
                    {
                        Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Verified" }
                    }
                },
                SortKeys = new Dictionary<string, ModIndexSortKeyDeclaration>(StringComparer.Ordinal)
                {
                    ["downloads"] = new()
                    {
                        Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Downloads" },
                        Order = "desc"
                    }
                }
            },
            Mods =
            [
                new ModIndexEntry
                {
                    Id = "everywhere.everything",
                    Name = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Everything" },
                    Description = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Fast file search." },
                    Author = "Sylinko",
                    Icon = inlineSvg,
                    License = new ModIndexLicense { Spdx = "BUSL-1.1", IsAcceptanceRequired = false },
                    Metadata = new ModIndexItemMetadata
                    {
                        Category = "search",
                        Labels = ["verified"],
                        Keywords = ["file", "ntfs"],
                        SortValues = new Dictionary<string, double?>(StringComparer.Ordinal) { ["downloads"] = 5000 }
                    },
                    IsDefaultEnabled = true,
                    Channels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["stable"] = "1.0.0",
                        ["v1.x"] = "1.0.0"
                    },
                    Versions =
                    [
                        new ModIndexVersion
                        {
                            Version = "1.0.0",
                            PublishedAt = 1790476800,
                            Readme = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["en"] = "https://docs.example.com/everything?lang=en#usage"
                            },
                            Entry = new PlatformModEntry
                            {
                                Variants =
                                [
                                    new ModPlatformVariant
                                    {
                                        Platform = "win-x64",
                                        Assembly = "Everything.dll",
                                        Type = "Everywhere.Mods.Everything.EverythingMod",
                                        Framework = "net11.0-windows10.0.19041.0",
                                        ModApiVersion = "[1.0.0,2.0.0)",
                                        HostVersion = "[1.0.0]",
                                        Assemblies = ["Everything.dll"]
                                    }
                                ]
                            },
                            Artifact = new ModArtifact
                            {
                                Url = "../everywhere.everything-1.0.0/everywhere.everything-1.0.0.zip",
                                Size = 4096,
                                Sha256 = SampleSha256,
                                Signature = ModSignature.SignArtifact("everywhere.everything", "1.0.0", SampleSha256, 4096, privateKeyPem)
                            }
                        }
                    ]
                },
                new ModIndexEntry
                {
                    Id = "community.avatar",
                    Name = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Avatar Demo" },
                    Icon = "https://avatars.example.com/u/42?v=4",
                    Versions =
                    [
                        new ModIndexVersion
                        {
                            Version = "1.0.0",
                            PublishedAt = 1790476800,
                            Entry = new UniversalModEntry
                            {
                                Assembly = "Avatar.dll",
                                Type = "Avatar.Entry",
                                Framework = "net11.0",
                                ModApiVersion = "[1.0.0,2.0.0)",
                                Assemblies = ["Avatar.dll"]
                            },
                            Artifact = new ModArtifact
                            {
                                Url = "packages/community.avatar/1.0.0/community.avatar-1.0.0.zip",
                                Size = 2048,
                                Sha256 = SampleSha256,
                                Signature = ModSignature.SignArtifact("community.avatar", "1.0.0", SampleSha256, 2048, privateKeyPem)
                            }
                        }
                    ]
                }
            ]
        };

        var signed = unsigned with { Signature = ModSignature.SignIndex(unsigned, privateKeyPem) };
        ModIndexFile.Write(indexPath, signed);

        // Future optional properties on index nodes must be ignored cleanly for forward compatibility.
        var document = JsonNode.Parse(File.ReadAllText(indexPath))?.AsObject()
            ?? throw new AssertionException("Missing generated index.");
        document["futureField"] = "ignored";
        File.WriteAllText(indexPath, document.ToJsonString());

        var githubUri = new Uri("https://github.com/Sylinko/Everywhere-Mods/releases/download/index/index.json");
        var giteeUri = new Uri("https://gitee.com/Sylinko/Everywhere-Mods/releases/download/index/index.json");
        var loaded = ModIndexFile.Read(indexPath, githubUri);

        var artifactRef = loaded.Mods[0].Versions[0].Artifact.Url;
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Mods[0].IsDefaultEnabled, Is.True);
            Assert.That(loaded.Mods[0].Versions[0].Entry, Is.TypeOf<PlatformModEntry>());
            Assert.That(
                ModIndexFile.ResolveAssetUri(githubUri, artifactRef).AbsoluteUri,
                Is.EqualTo(
                    "https://github.com/Sylinko/Everywhere-Mods/releases/download/everywhere.everything-1.0.0/everywhere.everything-1.0.0.zip"));
            Assert.That(
                ModIndexFile.ResolveAssetUri(giteeUri, artifactRef).AbsoluteUri,
                Is.EqualTo(
                    "https://gitee.com/Sylinko/Everywhere-Mods/releases/download/everywhere.everything-1.0.0/everywhere.everything-1.0.0.zip"));
        });
    }

    /// <summary>
    /// Undeclared taxonomy references, oversized inline icons, bad signatures, and unsafe URL schemes are rejected.
    /// </summary>
    [Test]
    public void Validate_InvalidMetadataOrSecurityViolations_ThrowsModValidationException()
    {
        var (privateKeyPem, publicKey) = ModSignature.GenerateKeyPair();
        var artifactSignature = ModSignature.SignArtifact("acme.example", "1.0.0", SampleSha256, 1024, privateKeyPem);
        var baseline = new ModIndex
        {
            Format = 1,
            Id = "community.test",
            Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Community" },
            GeneratedAt = 1790476800,
            PublicKey = publicKey,
            Metadata = new ModIndexMetadata
            {
                Categories = new Dictionary<string, ModIndexDeclaration>(StringComparer.Ordinal)
                {
                    ["tools"] = new()
                    {
                        Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Tools" }
                    }
                },
                SortKeys = new Dictionary<string, ModIndexSortKeyDeclaration>(StringComparer.Ordinal)
                {
                    ["stars"] = new()
                    {
                        Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Stars" },
                        Order = "desc"
                    }
                }
            },
            Mods =
            [
                new ModIndexEntry
                {
                    Id = "acme.example",
                    Channels = new Dictionary<string, string>(StringComparer.Ordinal) { ["stable"] = "1.0.0" },
                    Versions =
                    [
                        new ModIndexVersion
                        {
                            Version = "1.0.0",
                            PublishedAt = 1790476800,
                            Entry = new UniversalModEntry
                            {
                                Assembly = "Acme.dll",
                                Type = "Acme.Entry",
                                Framework = "net11.0",
                                ModApiVersion = "[1.0.0,2.0.0)",
                                Assemblies = ["Acme.dll"]
                            },
                            Artifact = new ModArtifact
                            {
                                Url = "packages/acme.example/1.0.0/acme.example-1.0.0.zip",
                                Size = 1024,
                                Sha256 = SampleSha256,
                                Signature = artifactSignature
                            }
                        }
                    ]
                }
            ]
        };

        var signed = baseline with { Signature = ModSignature.SignIndex(baseline, privateKeyPem) };

        Assert.Throws<ModValidationException>(() => ModIndexFile.Validate(
            signed with
            {
                Mods = [signed.Mods[0] with { Metadata = new ModIndexItemMetadata { Category = "missing" } }]
            }));

        var oversizedIcon = $"data:image/png;base64,{Convert.ToBase64String(new byte[ModIndexFile.MaxInlineIconBytes + 1])}";
        Assert.Throws<ModValidationException>(() => ModIndexFile.Validate(
            signed with
            {
                Mods = [signed.Mods[0] with { Icon = oversizedIcon }]
            }));

        Assert.Throws<ModValidationException>(() => ModIndexFile.Validate(
            signed with
            {
                Mods = [signed.Mods[0] with { Channels = new Dictionary<string, string>(StringComparer.Ordinal) { ["stable"] = "9.9.9" } }]
            }));

        Assert.Throws<ModValidationException>(() => ModIndexFile.Validate(
            signed with
            {
                Mods =
                [
                    signed.Mods[0] with
                    {
                        Metadata = new ModIndexItemMetadata
                        {
                            SortValues = new Dictionary<string, double?>(StringComparer.Ordinal) { ["stars"] = double.NaN }
                        }
                    }
                ]
            }));

        Assert.Throws<ModValidationException>(() => ModIndexFile.Validate(
            signed with
            {
                Signature = null
            }));

        var httpsIndex = new Uri("https://mods.example/index.json");
        var fileIndex = new Uri("file:///C:/mods/index.json");
        Assert.Multiple(() =>
        {
            Assert.That(
                ModIndexFile.ResolveAssetUri(httpsIndex, "http://mirror.example/mod.zip?v=1#top").AbsoluteUri,
                Is.EqualTo("http://mirror.example/mod.zip?v=1#top"));
            Assert.That(
                ModIndexFile.ResolveAssetUri(fileIndex, "packages/mod.zip").AbsoluteUri,
                Is.EqualTo("file:///C:/mods/packages/mod.zip"));
            Assert.Throws<ModValidationException>(() => ModIndexFile.ResolveAssetUri(httpsIndex, "file:///C:/mod.zip"));
            Assert.Throws<ModValidationException>(() => ModIndexFile.ResolveAssetUri(httpsIndex, "javascript:alert(1)"));
            Assert.Throws<ModValidationException>(() => ModIndexFile.ResolveAssetUri(httpsIndex, "//evil.example/mod.zip"));
            Assert.Throws<ModValidationException>(() => ModIndexFile.ResolveAssetUri(httpsIndex, "https://user:pass@example.com/mod.zip"));
            Assert.Throws<ModValidationException>(() => ModIndexFile.ResolveAssetUri(fileIndex, "javascript:alert(1)"));
        });
    }
}
