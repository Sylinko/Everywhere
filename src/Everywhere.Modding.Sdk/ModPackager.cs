using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Sdk;

/// <summary>
/// Packages explicit build outputs while excluding host-owned and dependency-mod managed identities.
/// </summary>
internal static class ModPackager
{
    /// <summary>
    /// Creates a ZIP and generated manifest from the staged pack directory written by MSBuild targets.
    /// </summary>
    public static string Pack(string packDirectory)
    {
        var fullPackDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packDirectory));
        var properties = ReadProperties(Path.Combine(fullPackDirectory, "properties.txt"));
        var assemblyPath = GetRequiredProperty(properties, "AssemblyPath");
        var referenceList = GetRequiredProperty(properties, "ReferenceList");
        var payloadList = properties.TryGetValue("PayloadList", out var explicitPayload) && !string.IsNullOrWhiteSpace(explicitPayload)
            ? explicitPayload
            : Path.Combine(fullPackDirectory, "payload.txt");
        var modId = GetRequiredProperty(properties, "ModId");
        var version = GetRequiredProperty(properties, "Version");
        var entryType = GetRequiredProperty(properties, "EntryType");
        var framework = GetRequiredProperty(properties, "Framework");
        var hostRange = properties.GetValueOrDefault("HostRange", "");
        var apiRange = properties.GetValueOrDefault("ApiRange", "");
        var rawPlatform = properties.GetValueOrDefault("Platform", "");
        var platform = rawPlatform == "universal" ? "" : rawPlatform;
        var outputPath = GetRequiredProperty(properties, "OutputPath");
        var projectDirectory = GetRequiredProperty(properties, "ProjectDirectory");

        var host = JsonSerializer.Deserialize(
                File.ReadAllText(referenceList + ".host.json"),
                SdkJsonSerializerContext.Default.ModHostDescriptor)
            ?? throw new ModValidationException("MissingHostImport", "The build has no host import record.");
        var hostAssemblies = File.ReadAllLines(referenceList + ".runtime.txt")
            .Select(AssemblyName.GetAssemblyName)
            .ToDictionary(GetIdentityKey, StringComparer.OrdinalIgnoreCase);
        var hostImplementationNames = File.ReadAllLines(referenceList)
            .Select(path => AssemblyName.GetAssemblyName(path).Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dependencies = ReadDependencies(fullPackDirectory);
        var dependencyAssemblies = dependencies
            .SelectMany(dependency => dependency.Assemblies)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entryAssembly = Path.GetFileName(assemblyPath);

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var explicitPackFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasHostImplementationReference = false;
        var contractName = typeof(IMod).Assembly.GetName().Name;

        foreach (var outputItem in File.ReadAllLines(payloadList)
                     .Where(line => !string.IsNullOrWhiteSpace(line))
                     .Append(assemblyPath + "|" + entryAssembly + "|runtime||")
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var parts = outputItem.Split('|', 5);
            var path = parts[0];
            var rawTarget = parts[1].Replace('\\', '/');
            var assetType = parts[2];
            var packagePath = parts.Length > 3 ? parts[3] : "";
            var recursiveDir = parts.Length > 4 ? parts[4] : "";
            var name = assetType == "pack-file"
                ? ResolvePackagePath(path, rawTarget, packagePath, recursiveDir)
                : rawTarget;
            PackagePath.ValidateRelative(name);

            if (!File.Exists(path))
            {
                throw new ModValidationException("MissingBuildOutput", path);
            }

            switch (assetType)
            {
                case "runtime":
                case "resources":
                {
                    if (string.IsNullOrEmpty(platform) && name.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ModValidationException(
                            "PlatformRequiredForManagedRuntimeTargets",
                            $"Platform-specific managed assembly '{name}' requires EverywhereModPlatform and RuntimeIdentifier.");
                    }

                    // A malformed managed output is a build error, never a native-library fallback.
                    var identity = AssemblyName.GetAssemblyName(path);
                    if (hostAssemblies.TryGetValue(GetIdentityKey(identity), out var provided))
                    {
                        if (provided.Version < identity.Version ||
                            !(provided.GetPublicKeyToken() ?? []).SequenceEqual(identity.GetPublicKeyToken() ?? []))
                        {
                            throw new ModValidationException(
                                "HostDependencyConflict",
                                $"Host '{provided}' cannot replace build dependency '{identity}'.");
                        }

                        continue;
                    }

                    if (!string.Equals(name, entryAssembly, StringComparison.OrdinalIgnoreCase) &&
                        IsDependencyAssembly(identity, dependencyAssemblies))
                    {
                        continue;
                    }

                    managed.Add(name);

                    using var stream = File.OpenRead(path);
                    using var pe = new PEReader(stream);
                    var metadata = pe.GetMetadataReader();

                    foreach (var handle in metadata.AssemblyReferences)
                    {
                        var reference = metadata.GetAssemblyReference(handle);
                        var referenceName = metadata.GetString(reference.Name);

                        if (referenceName != contractName && hostImplementationNames.Contains(referenceName))
                        {
                            hasHostImplementationReference = true;
                        }
                    }

                    if (!files.TryAdd(name, path) && files[name] != path)
                    {
                        throw new ModValidationException("PackagePathConflict", $"Different build outputs map to '{name}'.");
                    }

                    break;
                }
                case "pack-file":
                {
                    if (managed.Contains(name) && files[name] != path)
                    {
                        throw new ModValidationException("PackagePathConflict", $"Different build outputs map to '{name}'.");
                    }

                    if (!explicitPackFiles.Add(name) && files[name] != path)
                    {
                        throw new ModValidationException("PackagePathConflict", $"Different pack inputs map to '{name}'.");
                    }

                    files[name] = path;
                    break;
                }
                case "package-file":
                case "native":
                case "content":
                {
                    if (IsDependencySidecar(name, dependencyAssemblies))
                    {
                        continue;
                    }

                    if (explicitPackFiles.Contains(name))
                    {
                        continue;
                    }

                    if (!files.TryAdd(name, path) && files[name] != path)
                    {
                        throw new ModValidationException("PackagePathConflict", $"Different build outputs map to '{name}'.");
                    }

                    break;
                }
                default:
                {
                    throw new ModValidationException("InvalidAssetType", $"Unsupported build asset type '{assetType}' for '{name}'.");
                }
            }
        }

        if (hasHostImplementationReference && string.IsNullOrWhiteSpace(hostRange))
        {
            throw new ModValidationException(
                "MissingHostRange",
                "Packaging requires EverywhereHostVersion; an exact range for the imported host is a valid starting point.");
        }

        if (!string.IsNullOrWhiteSpace(hostRange) && !ModCompatibility.IsSatisfied(hostRange, host.HostVersion))
        {
            throw new ModValidationException("BuildHostOutsideRange", "The declared host range excludes the imported build host.");
        }

        if (string.IsNullOrWhiteSpace(apiRange))
        {
            var apiVersion = ModCompatibility.ParseVersion(host.ModApiVersion);
            apiRange = apiVersion.IsPrerelease
                ? $"[{apiVersion.ToNormalizedString()}]"
                : $"[{apiVersion.ToNormalizedString()},{(long)apiVersion.Major + 1}.0.0-0)";
        }

        if (!ModCompatibility.IsSatisfied(apiRange, host.ModApiVersion))
        {
            throw new ModValidationException("BuildApiOutsideRange", "The declared Mod API range excludes the imported build contract.");
        }

        var packageMetadata = ReadPackageMetadata(properties, projectDirectory, files);
        var distinctManaged = managed.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var normalizedHostRange = string.IsNullOrWhiteSpace(hostRange) ? null : hostRange;

        ModEntry entry = string.IsNullOrEmpty(platform)
            ? new UniversalModEntry
            {
                Assembly = entryAssembly,
                Type = entryType,
                Framework = framework,
                ModApiVersion = apiRange,
                HostVersion = normalizedHostRange,
                Assemblies = distinctManaged,
                Dependencies = dependencies
            }
            : new PlatformModEntry
            {
                Variants =
                [
                    new ModPlatformVariant
                    {
                        Platform = platform,
                        Assembly = entryAssembly,
                        Type = entryType,
                        Framework = framework,
                        ModApiVersion = apiRange,
                        HostVersion = normalizedHostRange,
                        Assemblies = distinctManaged,
                        Dependencies = dependencies
                    }
                ]
            };

        var manifest = new ModManifest
        {
            Id = modId,
            Version = version,
            Author = packageMetadata.Author,
            Icon = packageMetadata.Icon,
            Homepage = packageMetadata.Homepage,
            Repository = packageMetadata.Repository,
            License = packageMetadata.License,
            Entry = entry
        };
        ApplyPresentation(manifest, fullPackDirectory, projectDirectory, files);
        var manifestBytes = ModManifestFile.SerializeToUtf8Bytes(manifest);

        var output = Path.GetFullPath(outputPath);
        var stampPath = Path.Combine(
            Path.GetDirectoryName(fullPackDirectory) ?? fullPackDirectory,
            Path.GetFileName(fullPackDirectory) + ".inputs");
        var inputFingerprint = CreatePackFingerprint(output, manifestBytes, files);
        if (IsArchiveUpToDate(output, stampPath, inputFingerprint))
        {
            return output;
        }

        var parent = Path.GetDirectoryName(output) ?? ".";
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".mod-pack-" + Guid.NewGuid().ToString("N"));
        var temporaryArchive = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(staging);

        try
        {
            foreach (var (relative, source) in files)
            {
                var destination = PackagePath.Resolve(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? staging);
                File.Copy(source, destination);
            }

            File.WriteAllBytes(Path.Combine(staging, ModManifestFile.FileName), manifestBytes);
            // Delete the stamp before creating or replacing the archive so a failed run never leaves a stale cache marker.
            if (File.Exists(stampPath))
            {
                File.Delete(stampPath);
            }

            ZipFile.CreateFromDirectory(staging, temporaryArchive);
            File.Move(temporaryArchive, output, overwrite: true);
            File.WriteAllText(stampPath, inputFingerprint);
            return output;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
            if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive);
        }
    }

    private static bool IsArchiveUpToDate(string outputPath, string stampPath, string inputFingerprint)
    {
        return File.Exists(outputPath) && File.Exists(stampPath) && File.ReadAllText(stampPath) == inputFingerprint;
    }

    private static string CreatePackFingerprint(
        string outputPath,
        byte[] manifestBytes,
        Dictionary<string, string> files
    )
    {
        var builder = new StringBuilder();
        builder.AppendLine(outputPath);
        builder.AppendLine(Convert.ToHexStringLower(SHA256.HashData(manifestBytes)));

        AppendToolStamp(builder, typeof(ModPackager).Assembly.Location);
        AppendToolStamp(builder, typeof(ModManifestFile).Assembly.Location);

        foreach (var (relative, source) in files.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(source);
            builder.Append(relative)
                .Append('|').Append(info.FullName)
                .Append('|').Append(info.Length)
                .Append('|').AppendLine(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static void AppendToolStamp(StringBuilder builder, string assemblyPath)
    {
        if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
        {
            return;
        }

        var info = new FileInfo(assemblyPath);
        builder.Append(info.FullName)
            .Append('|').Append(info.Length)
            .Append('|').AppendLine(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
    }

    private static string ResolvePackagePath(string sourcePath, string assignedTargetPath, string packagePath, string recursiveDir)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return assignedTargetPath.Replace('\\', '/');
        }

        var normalized = packagePath.Trim().Replace('\\', '/');
        var normalizedRecursive = recursiveDir.Replace('\\', '/').TrimStart('/');
        var fileName = Path.GetFileName(sourcePath);

        if (normalized is "/" or ".")
        {
            return normalizedRecursive + fileName;
        }

        var isDirectoryPrefix = normalized.EndsWith('/') ||
            !string.IsNullOrEmpty(normalizedRecursive) ||
            (!Path.HasExtension(normalized) && !string.Equals(Path.GetFileName(normalized), fileName, StringComparison.OrdinalIgnoreCase));

        if (!isDirectoryPrefix)
        {
            return normalized.TrimStart('/');
        }

        var prefix = normalized.Trim('/');
        return string.IsNullOrEmpty(prefix) ? normalizedRecursive + fileName : $"{prefix}/{normalizedRecursive}{fileName}";
    }

    private static Dictionary<string, string> ReadProperties(string path)
    {
        if (!File.Exists(path))
        {
            throw new ModValidationException("MissingPackProperties", $"Pack properties file '{path}' does not exist.");
        }

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            var separatorIndex = rawLine.IndexOf('=');
            if (separatorIndex <= 0)
            {
                throw new ModValidationException("InvalidPackProperty", $"Malformed pack property line '{rawLine}'.");
            }

            var key = rawLine[..separatorIndex].Trim();
            var value = rawLine[(separatorIndex + 1)..];
            properties[key] = value;
        }

        return properties;
    }

    private static string GetRequiredProperty(Dictionary<string, string> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ModValidationException("MissingPackProperty", $"Required pack property '{key}' was not provided.");
        }

        return value;
    }

    private static string? GetOptionalProperty(Dictionary<string, string> properties, string key)
    {
        return properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    private static bool IsDependencyAssembly(AssemblyName identity, HashSet<string> dependencyAssemblies)
    {
        if (dependencyAssemblies.Count == 0 || identity.Name is null) return false;
        if (dependencyAssemblies.Contains(identity.Name)) return true;

        return !string.IsNullOrEmpty(identity.CultureName) &&
            identity.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) &&
            dependencyAssemblies.Contains(identity.Name[..^".resources".Length]);
    }

    private static bool IsDependencySidecar(string name, HashSet<string> dependencyAssemblies)
    {
        if (dependencyAssemblies.Count == 0) return false;

        var extension = Path.GetExtension(name);
        if (!extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return dependencyAssemblies.Contains(Path.GetFileNameWithoutExtension(name));
    }

    /// <summary>
    /// Builds package-level display maps from author items and conventional README files.
    /// Explicit mappings win before conventions are considered, independently of item ordering.
    /// </summary>
    private static void ApplyPresentation(
        ModManifest manifest,
        string packDirectory,
        string projectDirectory,
        Dictionary<string, string> files
    )
    {
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitReadmes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var localizationDirectory = Path.Combine(packDirectory, "i18n");
        var localizationsPath = Path.Combine(localizationDirectory, "localizations.txt");
        if (File.Exists(localizationsPath))
        {
            foreach (var tag in File.ReadAllLines(localizationsPath))
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;

                var language = tag.ToLowerInvariant();
                if (!languages.Add(language))
                {
                    throw new ModValidationException("DuplicateTranslation", $"Duplicate localization for language '{language}'.");
                }

                AddTranslation(manifest.Name, language, ReadPresentationField(localizationDirectory, tag, "name"));
                AddTranslation(manifest.Description, language, ReadPresentationField(localizationDirectory, tag, "description"));
                var readme = ReadPresentationField(localizationDirectory, tag, "readme").Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(readme)) continue;

                AddReadme(manifest, language, readme, projectDirectory, files);
                explicitReadmes.Add(language);
                explicitSources.Add(Path.GetFullPath(files[readme]));
            }
        }

        var readmesPath = Path.Combine(packDirectory, "readmes.txt");
        if (!File.Exists(readmesPath)) return;

        foreach (var path in File.ReadAllLines(readmesPath))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            var stem = Path.GetFileNameWithoutExtension(path);
            var language = stem.Equals("README", StringComparison.OrdinalIgnoreCase) ? "en" : stem["README.".Length..].ToLowerInvariant();
            var source = files.TryGetValue(path, out var payloadSource) ? payloadSource : PackagePath.Resolve(projectDirectory, path);
            if (explicitReadmes.Contains(language) || explicitSources.Contains(Path.GetFullPath(source))) continue;

            AddReadme(manifest, language, path, projectDirectory, files);
        }
    }

    /// <summary>
    /// Reads a projected MSBuild metadata value, removing only the task's final line terminator.
    /// </summary>
    private static string ReadPresentationField(string directory, string language, string field)
    {
        var path = Path.Combine(directory, language + "." + field + ".txt");
        if (!File.Exists(path)) return "";

        var text = File.ReadAllText(path);
        return text.EndsWith(Environment.NewLine, StringComparison.Ordinal) ? text[..^Environment.NewLine.Length] : text;
    }

    /// <summary>
    /// Leaves omitted project fields out of the manifest instead of blocking locale fallback.
    /// </summary>
    private static void AddTranslation(Dictionary<string, string> translations, string language, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) translations.Add(language, value);
    }

    /// <summary>
    /// Includes a README at its declared relative path and rejects ambiguous language/file mappings.
    /// </summary>
    private static void AddReadme(
        ModManifest manifest,
        string language,
        string path,
        string projectDirectory,
        Dictionary<string, string> files
    )
    {
        PackagePath.ValidateRelative(path);
        // A normal Content item may already supply this package path, including a linked shared file.
        // Otherwise the declaration includes a file at the same relative path in the author project.
        if (!files.ContainsKey(path))
        {
            var source = PackagePath.Resolve(projectDirectory, path);
            if (!File.Exists(source)) throw new ModValidationException("MissingReadme", source);

            files.Add(path, source);
        }

        if (!manifest.Readme.TryAdd(language, path))
        {
            throw new ModValidationException("DuplicateTranslation", $"Multiple README files for language '{language}'. Specify Readme explicitly.");
        }
    }

    /// <summary>
    /// Matches deployment identities without conflating satellites of different cultures.
    /// Version and public-key compatibility are checked after finding the host provider.
    /// </summary>
    private static string GetIdentityKey(AssemblyName identity) => identity.Name + "|" + identity.CultureName;

    private static ModDependency[] ReadDependencies(string packDirectory)
    {
        var dependenciesPath = Path.Combine(packDirectory, "dependencies.txt");
        if (!File.Exists(dependenciesPath))
        {
            return [];
        }

        var dependencies = new List<ModDependency>();
        foreach (var rawLine in File.ReadAllLines(dependenciesPath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split('|', 4);
            if (parts.Length != 4)
            {
                throw new ModValidationException("InvalidDependency", $"Malformed dependency declaration '{line}'.");
            }

            var id = parts[0].Trim();
            var version = parts[1].Trim();
            var optionalText = parts[2].Trim();
            var isOptional = false;
            if (!string.IsNullOrEmpty(optionalText) && !bool.TryParse(optionalText, out isOptional))
            {
                throw new ModValidationException("InvalidDependency", $"Invalid Optional flag '{optionalText}' for dependency '{id}'.");
            }

            var assemblies = parts[3].Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            dependencies.Add(
                new ModDependency
                {
                    Id = id,
                    Version = version,
                    IsOptional = isOptional,
                    Assemblies = assemblies
                });
        }

        return [.. dependencies];
    }

    private static PackageMetadata ReadPackageMetadata(
        Dictionary<string, string> properties,
        string projectDirectory,
        Dictionary<string, string> files
    )
    {
        var author = GetOptionalProperty(properties, "Author");
        var homepage = GetOptionalProperty(properties, "Homepage");
        var repositoryUrl = GetOptionalProperty(properties, "Repository");
        var repositoryDirectory = GetOptionalProperty(properties, "RepositoryDirectory")?.Replace('\\', '/');
        if (repositoryDirectory is not null && repositoryUrl is null)
        {
            throw new ModValidationException("InvalidUrl", "EverywhereModRepositoryDirectory requires EverywhereModRepository.");
        }

        var repository = repositoryUrl is null ? null : new ModRepositoryInfo { Url = repositoryUrl, Directory = repositoryDirectory };

        var icon = GetOptionalProperty(properties, "Icon")?.Replace('\\', '/');
        if (icon is not null)
        {
            IncludeOptionalPackageFile(icon, projectDirectory, files, "MissingIcon");
        }

        var licenseSpdx = GetOptionalProperty(properties, "License");
        var licenseFile = GetOptionalProperty(properties, "LicenseFile")?.Replace('\\', '/');
        var licenseAcceptanceText = GetOptionalProperty(properties, "RequireLicenseAcceptance");
        if (licenseSpdx is null && (licenseFile is not null || licenseAcceptanceText is not null))
        {
            throw new ModValidationException(
                "InvalidLicense",
                "EverywhereModLicense is required when license file or acceptance policy is specified.");
        }

        if (licenseSpdx is null)
        {
            return new PackageMetadata(author, icon, homepage, repository, null);
        }

        var isAcceptanceRequired = false;
        if (licenseAcceptanceText is not null && !bool.TryParse(licenseAcceptanceText, out isAcceptanceRequired))
        {
            throw new ModValidationException(
                "InvalidLicense",
                $"Invalid EverywhereModRequireLicenseAcceptance value '{licenseAcceptanceText}'.");
        }

        if (licenseFile is not null)
        {
            IncludeOptionalPackageFile(licenseFile, projectDirectory, files, "MissingLicense");
        }

        var thirdPartyFiles = files.Keys
            .Where(path =>
                path.StartsWith("Licenses/", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(path, licenseFile, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var license = new ModLicense
        {
            Spdx = licenseSpdx,
            File = licenseFile,
            IsAcceptanceRequired = isAcceptanceRequired,
            ThirdPartyFiles = thirdPartyFiles
        };

        return new PackageMetadata(author, icon, homepage, repository, license);
    }

    private static void IncludeOptionalPackageFile(
        string relativePath,
        string projectDirectory,
        Dictionary<string, string> files,
        string missingCode
    )
    {
        PackagePath.ValidateRelative(relativePath);
        if (files.ContainsKey(relativePath)) return;

        var source = PackagePath.Resolve(projectDirectory, relativePath);
        if (!File.Exists(source))
        {
            throw new ModValidationException(missingCode, source);
        }

        files.Add(relativePath, source);
    }

    private sealed record PackageMetadata(
        string? Author,
        string? Icon,
        string? Homepage,
        ModRepositoryInfo? Repository,
        ModLicense? License
    );
}
