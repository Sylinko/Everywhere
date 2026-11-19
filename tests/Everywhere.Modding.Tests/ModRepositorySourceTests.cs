using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Hosting.Sources;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Tests;

/// <summary>
/// Exercises embedded streams, local directory sources, Happy-Eyeballs HTTP mirror racing, ETag caching, and hash/signature enforcement.
/// </summary>
public sealed class ModRepositorySourceTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "Everywhere.Modding.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Embedded sources verify ECDSA P-256 index + artifact signatures and import directly into ModPackageStore via streams.
    /// </summary>
    [Test]
    public async Task EmbeddedModSource_SignedStreamResources_VerifiesTrustAndImportsIntoPackageStore()
    {
        var (privateKey, publicKey) = ModSignature.GenerateKeyPair();
        var packageBytes = CreatePackageBytes("acme.embedded", "1.0.0", "embedded-payload");
        var indexBytes = CreateSignedIndexBytes(
            sourceId: "native",
            modId: "acme.embedded",
            version: "1.0.0",
            artifactUrl: "packages/acme.embedded-1.0.0.zip",
            packageBytes: packageBytes,
            privateKey: privateKey,
            publicKey: publicKey);

        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["index.json"] = indexBytes,
            ["packages/acme.embedded-1.0.0.zip"] = packageBytes,
            ["details/acme.embedded/README.md"] = Encoding.UTF8.GetBytes("# Embedded Guide")
        };

        var source = new EmbeddedModSource(
            "native",
            path => resources.TryGetValue(path, out var bytes) ? new MemoryStream(bytes, writable: false) : null,
            isSignatureRequired: true,
            trustedPublicKey: publicKey);

        var index = await source.FetchIndexAsync();
        Assert.That(index.Mods.Single().Id, Is.EqualTo("acme.embedded"));
        Assert.That(await source.ReadTextDocumentAsync("details/acme.embedded/README.md"), Is.EqualTo("# Embedded Guide"));

        var store = new ModPackageStore(Path.Combine(_root, "runtime"), Path.Combine(_root, "data"), Path.Combine(_root, "staging"));
        await using (var artifactStream = await source.OpenArtifactAsync("acme.embedded", index.Mods[0].Versions[0]))
        {
            var installed = await store.ImportAsync(artifactStream);
            Assert.That(File.ReadAllText(Path.Combine(installed.Directory, "Entry.dll")), Is.EqualTo("embedded-payload"));
        }

        // Reject pinned key mismatch.
        var (_, wrongPublicKey) = ModSignature.GenerateKeyPair();
        var wrongKeySource = new EmbeddedModSource(
            "native",
            path => resources.TryGetValue(path, out var bytes) ? new MemoryStream(bytes, writable: false) : null,
            isSignatureRequired: true,
            trustedPublicKey: wrongPublicKey);
        var mismatch = await Assert.ThrowsAsync<ModValidationException>(async () => await wrongKeySource.FetchIndexAsync());
        Assert.That(mismatch?.Code, Is.EqualTo("PublicKeyMismatch"));

        // Reject tampered archive bytes with matching size? Flip 1 byte while keeping exact size.
        resources["packages/acme.embedded-1.0.0.zip"][^1] ^= 0xFF;
        var hashError =
            await Assert.ThrowsAsync<ModValidationException>(async () => await source.OpenArtifactAsync("acme.embedded", index.Mods[0].Versions[0]));
        Assert.That(hashError?.Code, Is.EqualTo("ArtifactHashMismatch"));
    }

    /// <summary>
    /// Slow primary triggers Happy-Eyeballs staggered mirror race; relative release-layout URLs resolve against the winner.
    /// </summary>
    [Test]
    public async Task HttpModSource_SlowPrimaryAndFastMirror_RacesEndpointsAndResolvesRelativeUrlAgainstWinner()
    {
        var (privateKey, publicKey) = ModSignature.GenerateKeyPair();
        var packageBytes = CreatePackageBytes("acme.remote", "1.2.0", "from-gitee-mirror");
        var indexBytes = CreateSignedIndexBytes(
            sourceId: "official",
            modId: "acme.remote",
            version: "1.2.0",
            artifactUrl: "../v1.2.0/acme.remote-1.2.0.zip",
            packageBytes: packageBytes,
            privateKey: privateKey,
            publicKey: publicKey);

        var handler = new FakeHttpHandler(async (request, token) =>
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (url.StartsWith("https://github.example/", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
            }

            if (url == "https://gitee.example/repo/latest/index.json")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(indexBytes) };
                response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return response;
            }

            if (url == "https://gitee.example/repo/v1.2.0/acme.remote-1.2.0.zip")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(packageBytes) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        var source = new HttpModSource(
            client,
            new HttpModSourceOptions
            {
                Id = "official",
                PrimaryIndexUri = new Uri("https://github.example/repo/latest/index.json"),
                MirrorIndexUris = [new Uri("https://gitee.example/repo/latest/index.json")],
                CacheDirectory = Path.Combine(_root, "http-cache"),
                IsSignatureRequired = true,
                TrustedPublicKey = publicKey,
                StaggerDelay = TimeSpan.FromMilliseconds(20)
            });

        var index = await source.FetchIndexAsync();
        Assert.That(index.Mods.Single().Id, Is.EqualTo("acme.remote"));

        await using var artifactStream = await source.OpenArtifactAsync("acme.remote", index.Mods[0].Versions[0]);
        var store = new ModPackageStore(Path.Combine(_root, "runtime"), Path.Combine(_root, "data"), Path.Combine(_root, "staging"));
        var installed = await store.ImportAsync(artifactStream);
        Assert.That(File.ReadAllText(Path.Combine(installed.Directory, "Entry.dll")), Is.EqualTo("from-gitee-mirror"));
        Assert.That(handler.RequestedUrls, Does.Contain("https://gitee.example/repo/v1.2.0/acme.remote-1.2.0.zip"));
    }

    /// <summary>
    /// Fast primary completes before the mirror stagger timer fires, avoiding redundant mirror traffic.
    /// </summary>
    [Test]
    public async Task HttpModSource_FastPrimary_CancelsStaggeredMirrorBeforeRequest()
    {
        var packageBytes = CreatePackageBytes("acme.fast", "1.0.0", "fast");
        var indexBytes = CreateUnsignedIndexBytes("custom", "acme.fast", "1.0.0", "acme.fast.zip", packageBytes);

        var handler = new FakeHttpHandler((request, _) =>
        {
            if (request.RequestUri?.Host == "primary.example")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(indexBytes) });
            }

            throw new AssertionException("Mirror should not have been contacted when primary answered before stagger delay.");
        });

        using var client = new HttpClient(handler);
        var source = new HttpModSource(
            client,
            new HttpModSourceOptions
            {
                Id = "custom",
                PrimaryIndexUri = new Uri("https://primary.example/index.json"),
                MirrorIndexUris = [new Uri("https://mirror.example/index.json")],
                StaggerDelay = TimeSpan.FromMilliseconds(400)
            });

        var index = await source.FetchIndexAsync();
        Assert.That(index.Mods.Single().Id, Is.EqualTo("acme.fast"));
        Assert.That(handler.RequestedUrls, Is.EqualTo(new[] { "https://primary.example/index.json" }));
    }

    /// <summary>
    /// Concurrent refreshes coalesce onto a single request and persisted ETags honor 304 Not Modified responses.
    /// </summary>
    [Test]
    public async Task HttpModSource_ETagNotModifiedAndConcurrentRequests_ReusesDiskCacheAndDeduplicates()
    {
        var packageBytes = CreatePackageBytes("acme.cached", "1.0.0", "cached");
        var indexBytes = CreateUnsignedIndexBytes("cached", "acme.cached", "1.0.0", "acme.cached.zip", packageBytes);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new FakeHttpHandler(async (request, token) =>
        {
            if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == "\"etag-1\""))
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            await gate.Task.WaitAsync(token);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(indexBytes) };
            response.Headers.ETag = new EntityTagHeaderValue("\"etag-1\"");
            return response;
        });

        var cacheDir = Path.Combine(_root, "etag-cache");
        using var client = new HttpClient(handler);
        var source = new HttpModSource(
            client,
            new HttpModSourceOptions
            {
                Id = "cached",
                PrimaryIndexUri = new Uri("https://cache.example/index.json"),
                CacheDirectory = cacheDir
            });

        var task1 = source.FetchIndexAsync();
        var task2 = source.FetchIndexAsync();
        gate.SetResult();
        var results = await Task.WhenAll(task1, task2);
        Assert.That(results[0].Mods.Single().Id, Is.EqualTo("acme.cached"));
        Assert.That(handler.RequestedUrls.Count, Is.EqualTo(1));

        // Simulate fresh process with warm disk ETag cache receiving 304 Not Modified.
        var restartedSource = new HttpModSource(
            client,
            new HttpModSourceOptions
            {
                Id = "cached",
                PrimaryIndexUri = new Uri("https://cache.example/index.json"),
                CacheDirectory = cacheDir
            });
        var cachedIndex = await restartedSource.FetchIndexAsync();
        Assert.That(cachedIndex.Mods.Single().Id, Is.EqualTo("acme.cached"));
        Assert.That(handler.RequestedUrls.Count, Is.EqualTo(2));
    }

    /// <summary>
    /// Rejects streams exceeding declared artifact byte size and cleans up any temporary download file.
    /// </summary>
    [Test]
    public async Task HttpModSource_OversizedPayload_AbortsAtDeclaredSizeAndDeletesTempFile()
    {
        var packageBytes = CreatePackageBytes("acme.huge", "1.0.0", "small");
        var indexBytes = CreateUnsignedIndexBytes("huge", "acme.huge", "1.0.0", "huge.zip", packageBytes);
        var oversizedBytes = packageBytes.Concat(new byte[1024]).ToArray();

        var handler = new FakeHttpHandler((request, _) =>
        {
            var payload = request.RequestUri!.AbsolutePath.EndsWith("index.json", StringComparison.Ordinal)
                ? indexBytes
                : oversizedBytes;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        });

        var cacheDir = Path.Combine(_root, "size-cache");
        using var client = new HttpClient(handler);
        var source = new HttpModSource(
            client,
            new HttpModSourceOptions
            {
                Id = "huge",
                PrimaryIndexUri = new Uri("https://huge.example/index.json"),
                CacheDirectory = cacheDir
            });

        var index = await source.FetchIndexAsync();
        var error = await Assert.ThrowsAsync<ModValidationException>(async () =>
            await source.OpenArtifactAsync("acme.huge", index.Mods[0].Versions[0]));
        Assert.That(error?.Code, Is.EqualTo("ArtifactSizeMismatch"));
        Assert.That(Directory.GetFiles(Path.Combine(cacheDir, "downloads")), Is.Empty);
    }

    /// <summary>
    /// Verifies local directory source release-layout relative URIs and README reading limits.
    /// </summary>
    [Test]
    public async Task LocalDirectoryModSource_ReleaseLayoutAndReadme_ValidatesAndReadsBoundedText()
    {
        var repoDir = Path.Combine(_root, "local-repo", "latest");
        var tagDir = Path.Combine(_root, "local-repo", "v1.0.0");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(tagDir);

        var packageBytes = CreatePackageBytes("acme.local", "1.0.0", "local-bytes");
        await File.WriteAllBytesAsync(Path.Combine(tagDir, "acme.local.zip"), packageBytes);
        await File.WriteAllTextAsync(Path.Combine(tagDir, "README.md"), "# Local README");
        var indexBytes = CreateUnsignedIndexBytes("local", "acme.local", "1.0.0", "../v1.0.0/acme.local.zip", packageBytes);
        await File.WriteAllBytesAsync(Path.Combine(repoDir, ModIndexFile.FileName), indexBytes);

        var source = new LocalDirectoryModSource("local", repoDir);
        var index = await source.FetchIndexAsync();
        Assert.That(await source.ReadTextDocumentAsync("../v1.0.0/README.md"), Is.EqualTo("# Local README"));
        Assert.That(await source.ReadTextDocumentAsync("../v1.0.0/MISSING.md"), Is.Null);

        await using var stream = await source.OpenArtifactAsync("acme.local", index.Mods[0].Versions[0]);
        var store = new ModPackageStore(Path.Combine(_root, "runtime"), Path.Combine(_root, "data"), Path.Combine(_root, "staging"));
        var installed = await store.ImportAsync(stream);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(installed.Directory, "Entry.dll")), Is.EqualTo("local-bytes"));
    }

    private static byte[] CreateSignedIndexBytes(
        string sourceId,
        string modId,
        string version,
        string artifactUrl,
        byte[] packageBytes,
        string privateKey,
        string publicKey
    )
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(packageBytes));
        var artifactSig = ModSignature.SignArtifact(modId, version, sha256, packageBytes.LongLength, privateKey);
        var unsigned = BuildIndex(sourceId, modId, version, artifactUrl, packageBytes.LongLength, sha256, publicKey, artifactSig);
        var indexSig = ModSignature.SignIndex(unsigned, privateKey);
        var signed = unsigned with { Signature = indexSig };

        using var ms = new MemoryStream();
        ModIndexFile.Write(ms, signed);
        return ms.ToArray();
    }

    private static byte[] CreateUnsignedIndexBytes(
        string sourceId,
        string modId,
        string version,
        string artifactUrl,
        byte[] packageBytes
    )
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(packageBytes));
        var index = BuildIndex(sourceId, modId, version, artifactUrl, packageBytes.LongLength, sha256, publicKey: null, artifactSig: null);
        using var ms = new MemoryStream();
        ModIndexFile.Write(ms, index);
        return ms.ToArray();
    }

    private static ModIndex BuildIndex(
        string sourceId,
        string modId,
        string version,
        string artifactUrl,
        long size,
        string sha256,
        string? publicKey,
        string? artifactSig
    )
    {
        return new ModIndex
        {
            Id = sourceId,
            Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = sourceId },
            PublicKey = publicKey,
            Mods =
            [
                new ModIndexEntry
                {
                    Id = modId,
                    Versions =
                    [
                        new ModIndexVersion
                        {
                            Version = version,
                            Entry = new UniversalModEntry
                            {
                                Assembly = "Entry.dll",
                                Type = "Sample.Entry",
                                Framework = "net11.0",
                                ModApiVersion = "[1.0,2.0)",
                                Assemblies = ["Entry.dll"]
                            },
                            Artifact = new ModArtifact
                            {
                                Url = artifactUrl,
                                Size = size,
                                Sha256 = sha256,
                                Signature = artifactSig
                            }
                        }
                    ]
                }
            ]
        };
    }

    private static byte[] CreatePackageBytes(string modId, string version, string payload)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var manifest = archive.CreateEntry(ModManifestFile.FileName).Open())
            {
                manifest.Write(
                    Encoding.UTF8.GetBytes(
                        $$"""
                          {
                            "schemaVersion": 1,
                            "id": "{{modId}}",
                            "version": "{{version}}",
                            "entry": {
                              "$type": "universal",
                              "assembly": "Entry.dll",
                              "type": "Sample.Entry",
                              "framework": "net11.0",
                              "modApiVersion": "[1.0,2.0)",
                              "assemblies": ["Entry.dll"]
                            }
                          }
                          """));
            }

            using (var dll = archive.CreateEntry("Entry.dll").Open())
            {
                dll.Write(Encoding.UTF8.GetBytes(payload));
            }
        }

        return stream.ToArray();
    }

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        internal ConcurrentQueue<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Enqueue(request.RequestUri?.AbsoluteUri ?? string.Empty);
            return responder(request, cancellationToken);
        }
    }
}
