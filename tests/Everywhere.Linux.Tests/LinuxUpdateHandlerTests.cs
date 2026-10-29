using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Everywhere.Common;
using Everywhere.Linux.Common;

namespace Everywhere.Linux.Tests;

[TestFixture]
public sealed class LinuxUpdateHandlerTests
{
    [Test]
    public async Task Discovery_WhenOlderReleaseLacksLinuxAssets_KeepsNewestCompatibleRelease()
    {
        using var factory = new ReleaseClientFactory(JsonSerializer.Serialize(new[]
        {
            Release("99.0.0"), Release("98.0.0", hasAsset: false)
        }));
        var handler = new LinuxUpdateHandler(factory);
        var releases = await handler.GetReleasesAsync(UpdateChannel.Stable, CancellationToken.None);
        Assert.That(releases.Select(release => release.Metadata.Tag), Is.EqualTo(new[] { "v99.0.0" }));
        Assert.That(factory.Requests.All(uri => uri.Host == "api.github.com"), Is.True);
    }

    [Test]
    public void Discovery_WhenNewestReleaseIsIncomplete_DoesNotOfferAnOlderVersion()
    {
        using var factory = new ReleaseClientFactory(JsonSerializer.Serialize(new[]
        {
            Release("99.0.0", hasAsset: false), Release("98.0.0")
        }));
        var handler = new LinuxUpdateHandler(factory);
        Assert.ThrowsAsync<InvalidDataException>(() => handler.GetReleasesAsync(UpdateChannel.Stable, CancellationToken.None));
    }

    [Test]
    public async Task Discovery_WhenDraftOrDifferentChannel_IgnoresRelease()
    {
        using var factory = new ReleaseClientFactory(JsonSerializer.Serialize(new[]
        {
            Release("99.0.0", isDraft: true), Release("98.0.0-canary.1"), Release("97.0.0")
        }));
        var releases = await new LinuxUpdateHandler(factory).GetReleasesAsync(UpdateChannel.Stable, CancellationToken.None);
        Assert.That(releases.Single().Metadata.Tag, Is.EqualTo("v97.0.0"));
    }

    [Test]
    public void Discovery_WhenAssetUrlChanges_RejectsUnexpectedSource()
    {
        using var factory = new ReleaseClientFactory(JsonSerializer.Serialize(new[] { Release("99.0.0", isUnexpectedSource: true) }));
        Assert.ThrowsAsync<InvalidDataException>(() => new LinuxUpdateHandler(factory).GetReleasesAsync(UpdateChannel.Stable, CancellationToken.None));
    }

    [Test]
    public void ExecuteUpdate_WhenCalledDirectly_RefusesGraphicalInstallation()
    {
        using var factory = new ReleaseClientFactory("[]");
        Assert.Throws<InvalidOperationException>(() => new LinuxUpdateHandler(factory).ExecuteUpdateAsync("package.deb", CancellationToken.None));
    }

    private static object Release(string version, bool hasAsset = true, bool isDraft = false, bool isUnexpectedSource = false)
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var name = $"Everywhere-Linux-{arch}-v{version}.tar.zst";
        return new
        {
            tag_name = $"v{version}", draft = isDraft, prerelease = version.Contains("canary"),
            published_at = "2026-10-29T00:00:00Z", body = "Linux release",
            assets = hasAsset ? new[]
            {
                new
                {
                    name, state = "uploaded", size = 123, digest = "sha256:" + new string('a', 64),
                    browser_download_url = isUnexpectedSource ? "https://example.com/package" :
                        $"https://github.com/{LinuxUpdateHandler.ReleaseRepository}/releases/download/v{version}/{name}"
                }
            } : []
        };
    }

    private sealed class ReleaseClientFactory(string json) : HttpMessageHandler, IHttpClientFactory
    {
        public List<Uri> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri ?? throw new InvalidOperationException("No request URI."));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}