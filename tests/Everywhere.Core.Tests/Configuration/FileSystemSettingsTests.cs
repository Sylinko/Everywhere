using System.Text.Json.Nodes;
using Everywhere.Configuration;
using Everywhere.Configuration.Engine;
using Everywhere.I18N;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Core.Tests.Configuration;

public sealed class FileSystemSettingsTests
{
    [Test]
    public void ApprovalPaths_NormalizeSeparatorsAndDropDuplicates()
    {
        var settings = new FileSystemSettings();

        Assert.Multiple(() =>
        {
            Assert.That(settings.AddApprovalPath(" C:\\Source\\Everywhere\\** "), Is.True);
            Assert.That(settings.AddApprovalPath("C:/Source/Everywhere/**"), Is.False);
            Assert.That(settings.ApprovalPaths.Select(static item => item.Pattern), Is.EqualTo(["C:/Source/Everywhere/**"]));
        });
    }

    [Test]
    public void ArePathsApproved_UsesGlobRulesWithoutTouchingDisk()
    {
        var settings = new FileSystemSettings();
        settings.AddApprovalPath("C:\\Source\\Everywhere\\**");
        settings.AddApprovalPath("D:\\**\\3rd");
        settings.AddApprovalPath("G:\\exact_folder");

        Assert.Multiple(() =>
        {
            Assert.That(settings.ArePathsApproved(["C:\\Source\\Everywhere\\src\\Everywhere.Core\\File.cs"]), Is.True);
            Assert.That(settings.ArePathsApproved(["C:\\Source\\Other\\File.cs"]), Is.False);
            Assert.That(settings.ArePathsApproved(["D:\\Source\\Everywhere\\3rd"]), Is.True);
            Assert.That(settings.ArePathsApproved(["G:\\exact_folder"]), Is.True);
            Assert.That(settings.ArePathsApproved(["G:\\exact_folder\\File.cs"]), Is.False);
        });
    }

    [Test]
    public void ArePathsApproved_RequiresAllPathsToMatch()
    {
        var settings = new FileSystemSettings();
        settings.AddApprovalPath("C:\\Source\\Everywhere\\**");

        Assert.That(
            settings.ArePathsApproved(["C:\\Source\\Everywhere\\File.cs", "C:\\Source\\Other\\File.cs"]),
            Is.False);
    }

    [Test]
    public void ApprovalPath_InvalidPatternReportsValidationError()
    {
        EnsureLocaleManager();
        var item = new FileSystemApprovalPath("relative/path");

        Assert.Multiple(() =>
        {
            Assert.That(item.HasErrors, Is.True);
            Assert.That(item.GetErrors(nameof(FileSystemApprovalPath.Pattern)), Is.Not.Empty);
        });
    }

    [Test]
    public void SettingsSerialization_WritesApprovalPathsAsStringArray()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(serviceProvider);
        settings.Plugin.FileSystem.AddApprovalPath("C:\\Source\\Everywhere\\**");

        var root = SettingsEngineJson.SerializeToNode(settings.Plugin.FileSystem, typeof(FileSystemSettings))!.AsObject();
        var approvalPaths = root["ApprovalPaths"]!.AsArray();

        Assert.Multiple(() =>
        {
            Assert.That(approvalPaths, Has.Count.EqualTo(1));
            Assert.That(approvalPaths[0]!.GetValue<string>(), Is.EqualTo("C:/Source/Everywhere/**"));
        });
    }

    [Test]
    public void SettingsPatch_ReadsApprovalPathsFromStringArray()
    {
        var settings = new FileSystemSettings();
        var root = JsonNode.Parse("""{ "ApprovalPaths": ["D:\\**\\3rd", "F:/Source/Everywhere/*"] }""")!.AsObject();
        var binder = new SettingsPatchBinder();

        binder.Patch(root, settings);

        Assert.Multiple(() =>
        {
            Assert.That(binder.Diagnostics, Is.Empty);
            Assert.That(settings.ApprovalPaths.Select(static item => item.Pattern),
                Is.EqualTo(["D:/**/3rd", "F:/Source/Everywhere/*"]));
        });
    }

    private static void EnsureLocaleManager()
    {
        try
        {
            _ = LocaleManager.Shared;
        }
        catch (InvalidOperationException)
        {
            _ = new LocaleManager();
        }
    }
}
