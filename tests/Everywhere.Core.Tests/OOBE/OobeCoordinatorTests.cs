using Everywhere.Configuration;
using Everywhere.Configuration.Engine;
using Everywhere.Core.Tests.Configuration;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.OOBE;

public sealed class OobeCoordinatorTests
{
    [Test, NonParallelizable]
    public async Task SettingsEngineMigration_WhenFreshInstall_InitializesVersionZero()
    {
        using var secretStore = new SecretStoreScope();
        var tempDir = Directory.CreateTempSubdirectory("everywhere-oobe-test-");
        try
        {
            var filePath = Path.Combine(tempDir.FullName, "settings.json");
            await using var services = new ServiceCollection().BuildServiceProvider();
            var settings = new Settings(services);
            using var engine = new SettingsEngine(settings, filePath, services, NullLoggerFactory.Instance);

            await engine.InitializeAsync(default);

            Assert.That(settings.CompletedOobeVersion, Is.Zero);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Test]
    public async Task SettingsEngineMigration_WhenLegacySettingsHaveAssistants_SetsLegacyBaseline()
    {
        var tempDir = Directory.CreateTempSubdirectory("everywhere-oobe-test-");
        try
        {
            var filePath = Path.Combine(tempDir.FullName, "settings.json");
            await File.WriteAllTextAsync(
                filePath,
                """
                {
                  "Version": "0.9.0-canary.20261115.28",
                  "Model": {
                    "CustomAssistants": [
                      { "Name": "Existing" }
                    ]
                  }
                }
                """);
            await using var services = new ServiceCollection().BuildServiceProvider();
            var settings = new Settings(services);
            using var engine = new SettingsEngine(settings, filePath, services, NullLoggerFactory.Instance);

            await engine.InitializeAsync(default);

            Assert.That(settings.CompletedOobeVersion, Is.EqualTo(OobeCoordinator.LegacyBaselineVersion));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Test]
    public async Task SettingsEngineMigration_WhenExplicitlyZeroEvenWithAssistant_PreservesZero()
    {
        var tempDir = Directory.CreateTempSubdirectory("everywhere-oobe-test-");
        try
        {
            var filePath = Path.Combine(tempDir.FullName, "settings.json");
            await File.WriteAllTextAsync(
                filePath,
                """
                {
                  "Version": "0.9.0-canary.20261115.28",
                  "CompletedOobeVersion": 0,
                  "Model": {
                    "CustomAssistants": [
                      { "Name": "Draft" }
                    ]
                  }
                }
                """);
            await using var services = new ServiceCollection().BuildServiceProvider();
            var settings = new Settings(services);
            using var engine = new SettingsEngine(settings, filePath, services, NullLoggerFactory.Instance);

            await engine.InitializeAsync(default);

            Assert.That(settings.CompletedOobeVersion, Is.Zero);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
