using System.Text.Json.Nodes;
using Everywhere.Common;
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
    public void ArePathsApproved_UsesNativeGlobRulesWithoutTouchingDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "everywhere-approval", "source");
        var otherRoot = Path.Combine(Path.GetTempPath(), "everywhere-approval", "other");
        var exactPath = Path.Combine(Path.GetTempPath(), "everywhere-approval", "exact");
        var settings = new FileSystemSettings();
        settings.AddApprovalPath(CreateDirectoryPattern(root));
        settings.AddApprovalPath(FileSystemApprovalPath.Normalize(Path.Combine(otherRoot, "**", "third")));
        settings.AddApprovalPath(exactPath);

        Assert.Multiple(() =>
        {
            Assert.That(settings.ArePathsApproved([Path.Combine(root, "src", "File.cs")]), Is.True);
            Assert.That(settings.ArePathsApproved([Path.Combine(Path.GetTempPath(), "outside", "File.cs")]), Is.False);
            Assert.That(settings.ArePathsApproved([Path.Combine(otherRoot, "nested", "third")]), Is.True);
            Assert.That(settings.ArePathsApproved([exactPath]), Is.True);
            Assert.That(settings.ArePathsApproved([Path.Combine(exactPath, "File.cs")]), Is.False);
        });
    }

    [Test]
    public void ArePathsApproved_RequiresAllPathsToMatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "everywhere-approval", "source");
        var settings = new FileSystemSettings();
        settings.AddApprovalPath(CreateDirectoryPattern(root));

        Assert.That(
            settings.ArePathsApproved([Path.Combine(root, "File.cs"), Path.Combine(Path.GetTempPath(), "outside", "File.cs")]),
            Is.False);
    }

    [Test]
    public void ArePathsApproved_AliasRuleIsNotResolvedAgainstFinalCandidate()
    {
        var root = CreateTemporaryDirectory();
        var targetDirectory = Path.Combine(root, "target");
        var linkDirectory = Path.Combine(root, "link");
        Directory.CreateDirectory(targetDirectory);

        try
        {
            CreateDirectoryLinkOrIgnore(linkDirectory, "target");
            var requestedPath = Path.Combine(linkDirectory, "file.txt");
            var resolvedPath = ResolveFinalPath(requestedPath);
            var settings = new FileSystemSettings();
            settings.AddApprovalPath(CreateDirectoryPattern(linkDirectory));

            Assert.That(settings.ArePathsApproved([resolvedPath]), Is.False);

            settings.AddApprovalPath(CreateDirectoryPattern(ResolveFinalPath(targetDirectory)));
            Assert.That(settings.ArePathsApproved([resolvedPath]), Is.True);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public void ArePathsApproved_UsesPlatformPathComparison()
    {
        var root = Path.Combine(Path.GetTempPath(), "everywhere-approval-case");
        var settings = new FileSystemSettings();
        settings.AddApprovalPath(CreateDirectoryPattern(root));
        var differentlyCasedPath = Path.Combine(root.ToUpperInvariant(), "FILE.TXT");

        Assert.That(
            settings.ArePathsApproved([differentlyCasedPath]),
            Is.EqualTo(PathUtilities.SystemPathComparison is StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void ArePathsApproved_UnixLiteralBackslashIsNotTreatedAsSeparator()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Backslash is a path separator on Windows.");

        var root = Path.Combine(Path.GetTempPath(), "everywhere-approval-backslash");
        var settings = new FileSystemSettings();
        settings.AddApprovalPath(Path.Combine(root, "literal", "name.txt"));

        Assert.That(settings.ArePathsApproved([Path.Combine(root, "literal\\name.txt")]), Is.False);
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

    private static string CreateDirectoryPattern(string path) =>
        FileSystemApprovalPath.Normalize(Path.GetFullPath(path)).TrimEnd('/') + "/**";

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "everywhere-approval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateDirectoryLinkOrIgnore(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Ignore($"Symbolic links are unavailable in this test environment: {ex.Message}");
        }
    }

    private static string ResolveFinalPath(string path)
    {
        var result = PathUtilities.ResolvePath(path, PathResolutionMode.FollowFinalComponent);
        return result.ResolvedPath ?? throw new AssertionException($"Could not resolve test path '{path}'.");
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
