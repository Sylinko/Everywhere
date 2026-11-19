using System.Security.Cryptography;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Tests;

/// <summary>
/// Verifies ECDSA P-256 signing and tamper detection for artifacts and repository indexes.
/// </summary>
public sealed class ModSignatureTests
{
    private const string SampleSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    /// Artifact signatures bind mod ID, normalized version, digest, and byte length without false positives.
    /// </summary>
    [Test]
    public void SignAndVerifyArtifact_DetectsTamperingAndRejectsWrongCurve()
    {
        var (privateKeyPem, publicKey) = ModSignature.GenerateKeyPair();
        Assert.Multiple(() =>
        {
            Assert.That(publicKey, Does.StartWith(ModSignature.AlgorithmPrefix));
            Assert.That(ModSignature.GetPublicKey(privateKeyPem), Is.EqualTo(publicKey));
        });

        var signature = ModSignature.SignArtifact("acme.example", "1.0.0", SampleSha256, 4096, privateKeyPem);
        ModSignature.ValidateSignatureFormat(signature);

        Assert.Multiple(() =>
        {
            Assert.That(ModSignature.VerifyArtifact("acme.example", "1.0.0", SampleSha256, 4096, signature, publicKey), Is.True);
            Assert.That(ModSignature.VerifyArtifact("acme.other", "1.0.0", SampleSha256, 4096, signature, publicKey), Is.False);
            Assert.That(ModSignature.VerifyArtifact("acme.example", "1.0.1", SampleSha256, 4096, signature, publicKey), Is.False);
            Assert.That(ModSignature.VerifyArtifact("acme.example", "1.0.0", new string('a', 64), 4096, signature, publicKey), Is.False);
            Assert.That(ModSignature.VerifyArtifact("acme.example", "1.0.0", SampleSha256, 4097, signature, publicKey), Is.False);
            Assert.That(ModSignature.VerifyArtifact("acme.example", "1.0.0", SampleSha256, 4096, "bad", publicKey), Is.False);
        });

        using var wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var wrongPublicKey = ModSignature.AlgorithmPrefix + Convert.ToBase64String(wrongCurve.ExportSubjectPublicKeyInfo());
        var wrongPrivateKeyPem = wrongCurve.ExportPkcs8PrivateKeyPem();
        Assert.Throws<ModValidationException>(() => ModSignature.ValidatePublicKey(wrongPublicKey));
        Assert.Throws<ModValidationException>(() => ModSignature.SignArtifact("acme.example", "1.0.0", SampleSha256, 4096, wrongPrivateKeyPem));
    }

    /// <summary>
    /// Canonical index signatures survive dictionary key reordering while catching any field mutation.
    /// </summary>
    [Test]
    public void SignAndVerifyIndex_IsOrderInvariantAndDetectsTampering()
    {
        var (privateKeyPem, publicKey) = ModSignature.GenerateKeyPair();
        var artifactSignature = ModSignature.SignArtifact("acme.example", "1.0.0", SampleSha256, 1024, privateKeyPem);

        var unsigned = new ModIndex
        {
            Format = 1,
            Id = "everywhere.official",
            Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["zh-hans"] = "测试源",
                ["en"] = "Test Feed"
            },
            GeneratedAt = 1790476800,
            PublicKey = publicKey,
            Mirrors = ["https://gitee.com/example/mods/releases/download/index/index.json"],
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
                                Url = "../acme.example-1.0.0/acme.example-1.0.0.zip",
                                Size = 1024,
                                Sha256 = SampleSha256,
                                Signature = artifactSignature
                            }
                        }
                    ]
                }
            ]
        };

        var signed = unsigned with { Signature = ModSignature.SignIndex(unsigned, privateKeyPem) };

        // Reverse dictionary insertion order to verify canonical payload stability.
        var reordered = signed with
        {
            Title = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = "Test Feed",
                ["zh-hans"] = "测试源"
            }
        };

        Assert.That(ModSignature.VerifyIndex(reordered, publicKey), Is.True);

        var tamperedMirror = signed with { Mirrors = ["https://evil.example/index.json"] };
        Assert.That(ModSignature.VerifyIndex(tamperedMirror, publicKey), Is.False);

        var tamperedVersion = signed with
        {
            Mods =
            [
                signed.Mods[0] with
                {
                    Versions =
                    [
                        signed.Mods[0].Versions[0] with
                        {
                            Artifact = signed.Mods[0].Versions[0].Artifact with { Url = "../tampered/mod.zip" }
                        }
                    ]
                }
            ]
        };
        Assert.That(ModSignature.VerifyIndex(tamperedVersion, publicKey), Is.False);
    }
}
