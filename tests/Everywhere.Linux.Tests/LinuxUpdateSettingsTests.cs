using System.Net;
using Everywhere.Common;
using Everywhere.Linux.Common;

namespace Everywhere.Linux.Tests;

[TestFixture]
public sealed class LinuxUpdateSettingsTests
{
    [Test]
    public void SavedSettings_WhenChannelAndAuthenticatedSocksProxyAreConfigured_UsesBothWithoutRewritingFile()
    {
        WithSettings("""
            {
              // The application accepts comments and trailing commas.
              "Common": { "UpdateChannel": "Canary", "IsAutomaticUpdateCheckEnabled": false, },
              "Proxy": {
                "IsEnabled": true, "Endpoint": "socks5://127.0.0.1:1080",
                "UseAuthentication": true, "Username": "test-user", "Password": "test-password",
                "BypassOnLocal": true, "BypassList": ".*\\.example\\.org",
              },
            }
            """, path =>
        {
            var original = File.ReadAllText(path);
            var settings = LinuxUpdateSettings.Load(path);
            Assert.That(settings.Channel, Is.EqualTo(UpdateChannel.Canary));
            using var handler = LinuxUpdateDownload.CreateHandler(settings.Proxy, null);
            var proxy = handler.Proxy ?? throw new InvalidOperationException("No proxy.");
            Assert.That(proxy.GetProxy(new Uri("https://api.github.com"))?.Scheme, Is.EqualTo("socks5"));
            Assert.That(proxy.IsBypassed(new Uri("https://a.example.org")), Is.True);
            Assert.That(proxy.IsBypassed(new Uri("http://localhost")), Is.True);
            Assert.That(proxy.Credentials?.GetCredential(new Uri("socks5://127.0.0.1:1080"), "Basic")?.UserName, Is.EqualTo("test-user"));
            Assert.That(File.ReadAllText(path), Is.EqualTo(original));
        });
    }

    [Test]
    public void SavedSettings_WhenAutomaticChecksAreDisabled_StillProvidesExplicitUpdateChannel()
    {
        WithSettings("""{"Common":{"UpdateChannel":"Stable","IsAutomaticUpdateCheckEnabled":false}}""", path =>
            Assert.That(LinuxUpdateSettings.Load(path).Channel, Is.EqualTo(UpdateChannel.Stable)));
    }

    [Test]
    public void SavedSettings_WhenChannelIsDefault_UsesSameResolutionAsGui()
    {
        WithSettings("""{"Common":{"UpdateChannel":"Unknown"}}""", path =>
            Assert.That(LinuxUpdateSettings.Load(path).Channel, Is.EqualTo(SoftwareUpdater.ResolveUpdateChannel(UpdateChannel.Unknown))));
    }

    [Test]
    public void SavedSettings_WhenExplicitOptionsOverrideInvalidValues_DoesNotBindOverriddenFields()
    {
        WithSettings("""{"Common":{"UpdateChannel":"invalid"},"Proxy":{"IsEnabled":"invalid"}}""", path =>
        {
            Assert.Throws<InvalidDataException>(() => LinuxUpdateSettings.Load(path));
            Assert.DoesNotThrow(() => LinuxUpdateSettings.Load(path, shouldReadChannel: false, shouldReadProxy: false));
        });
    }

    [Test]
    public void Proxy_WhenExplicitlyOverridden_ReplacesSavedProxyWithoutChangingSettings()
    {
        WithSettings("""{"Proxy":{"IsEnabled":true,"Endpoint":"http://old.example:8080"}}""", path =>
        {
            var settings = LinuxUpdateSettings.Load(path);
            using var direct = LinuxUpdateDownload.CreateHandler(settings.Proxy, "none");
            Assert.That(direct.UseProxy, Is.False);
            using var custom = LinuxUpdateDownload.CreateHandler(settings.Proxy, "http://new.example:8081");
            Assert.That(custom.Proxy?.GetProxy(new Uri("https://github.com"))?.Host, Is.EqualTo("new.example"));
            Assert.That(settings.Proxy.Endpoint, Is.EqualTo("http://old.example:8080"));
        });
    }

    [Test]
    public void SavedSettings_WhenMalformed_RefusesSilentDefaultFallback()
    {
        WithSettings("not JSON", path => Assert.Catch<System.Text.Json.JsonException>(() => LinuxUpdateSettings.Load(path)));
    }

    private static void WithSettings(string json, Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"everywhere-update-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, json);
            action(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}