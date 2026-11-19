using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Hosting.Installation;

/// <summary>
/// Imports complete ZIP packages or unpacked directories into distinct installation directories without mutating active files.
/// The application owns registration, desired selection and the decision that an installation may be retired.
/// </summary>
public sealed class ModPackageStore(string rootDirectory, string dataDirectory, string stagingDirectory)
{
    private static readonly byte[] FingerprintSeparator = [0];

    /// <summary>
    /// Gets the host-selected package-store root. The application uses a rebuildable runtime cache.
    /// </summary>
    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);

    /// <summary>
    /// Gets persistent user data, kept separate from installed code and temporary files.
    /// </summary>
    public string DataDirectory { get; } = Path.GetFullPath(dataDirectory);

    /// <summary>
    /// Gets temporary import storage on the same filesystem as the installation store.
    /// </summary>
    public string StagingDirectory { get; } = Path.GetFullPath(stagingDirectory);

    /// <summary>
    /// Extracts and validates a package before moving it to its final path. Cancellation is honored
    /// before publication; a returned installation is complete but has not been registered or activated.
    /// </summary>
    public async Task<ModInstallation> ImportAsync(Stream package, CancellationToken cancellationToken = default)
    {
        var installationId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(StagingDirectory, installationId);
        Directory.CreateDirectory(staging);

        try
        {
            await using (var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true))
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isDirectory = entry.FullName.EndsWith('/');
                    var path = isDirectory ? entry.FullName[..^1] : entry.FullName;
                    PackagePath.ValidateRelative(path);

                    if (!paths.Add(path))
                    {
                        throw new ModValidationException("DuplicatePackagePath", $"Duplicate archive path '{path}'.");
                    }

                    // Unix symlinks inside archives would change the meaning of later paths during extraction.
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    {
                        throw new ModValidationException("UnsupportedSymbolicLink", $"Archive symlink '{path}' is not supported.");
                    }

                    var destination = PackagePath.Resolve(staging, path);

                    if (isDirectory)
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    var parent = Path.GetDirectoryName(destination);
                    if (parent is not null)
                    {
                        Directory.CreateDirectory(parent);
                    }

                    await using var input = await entry.OpenAsync(cancellationToken);
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, cancellationToken);
                }
            }

            return Publish(staging, installationId, cancellationToken);
        }
        finally
        {
            // Best-effort deletion must not replace the original import exception.
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Copies an unpacked source package to a private installation. Source files are never executed in place.
    /// Symbolic links and directory junctions are followed during copy as long as no link is broken or cyclic.
    /// </summary>
    public async Task<ModInstallation> ImportDirectoryAsync(string source, CancellationToken cancellationToken = default)
    {
        var installationId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(StagingDirectory, installationId);
        Directory.CreateDirectory(staging);

        try
        {
            foreach (var file in EnumerateSourceFiles(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                var destination = PackagePath.Resolve(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? staging);
                await using var input = File.OpenRead(file);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken);
            }

            return Publish(staging, installationId, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Attempts to reopen an existing cached installation directory owned by this store without re-importing it.
    /// </summary>
    public bool TryOpen(string directory, [NotNullWhen(true)] out ModInstallation? installation)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var packagesRoot = Path.GetFullPath(Path.Combine(RootDirectory, "packages"));
        var packagesPrefix = packagesRoot + Path.DirectorySeparatorChar;
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

        if (!fullPath.StartsWith(packagesPrefix, comparison) || !Directory.Exists(fullPath))
        {
            installation = null;
            return false;
        }

        try
        {
            var manifest = ModManifestFile.Read(fullPath);
            ValidateDeclaredFiles(fullPath, manifest);

            var normalizedVersion = ModCompatibility.ParseVersion(manifest.Version).ToNormalizedString();
            var expectedPrefix = Path.Combine(packagesRoot, manifest.Id, normalizedVersion + "-");
            if (!fullPath.StartsWith(expectedPrefix, comparison) || fullPath.Length <= expectedPrefix.Length)
            {
                installation = null;
                return false;
            }

            var installationId = fullPath[expectedPrefix.Length..];
            if (installationId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                installation = null;
                return false;
            }

            installation = new ModInstallation
            {
                InstallationId = installationId,
                Directory = fullPath,
                Manifest = manifest
            };
            return true;
        }
        catch (Exception exception) when (exception is ModValidationException or IOException or UnauthorizedAccessException or JsonException)
        {
            installation = null;
            return false;
        }
    }

    /// <summary>
    /// Gets the stable writable data directory for a validated installation's mod ID.
    /// </summary>
    public string GetDataDirectory(ModInstallation installation) => GetDataDirectory(installation.Manifest.Id);

    /// <summary>
    /// Gets the stable writable data directory for a validated mod ID.
    /// </summary>
    public string GetDataDirectory(string modId)
    {
        if (!ModValidationRules.IsValidIdentifier(modId))
        {
            throw new ModValidationException("InvalidModId", $"Invalid mod identifier '{modId}'.");
        }

        var path = Path.Combine(DataDirectory, modId);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Tries to delete an installation only after the caller has released all users and selection references.
    /// Returns false for filesystem failures so the host can defer deletion without undoing activation.
    /// </summary>
    public bool TryRemove(ModInstallation installation) => TryRemoveDirectory(installation.Directory);

    /// <summary>
    /// Tries to delete an arbitrary package directory owned inside this store's <c>packages</c> root.
    /// </summary>
    public bool TryRemoveDirectory(string directory)
    {
        var root = Path.GetFullPath(Path.Combine(RootDirectory, "packages")) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!path.StartsWith(root, comparison))
        {
            throw new ModValidationException("InvalidInstallationPath", "The installation is outside this package store.");
        }

        return TryDeleteDirectory(path);
    }

    /// <summary>
    /// Computes a deterministic SHA-256 fingerprint over every relative path, length, and file payload
    /// inside an unpacked source directory so unchanged development folders skip redundant re-imports.
    /// </summary>
    public static async Task<string> ComputeDirectoryFingerprintAsync(string sourceDirectory, CancellationToken cancellationToken = default)
    {
        var files = EnumerateSourceFiles(sourceDirectory)
            .Select(path => (FullPath: path, RelativePath: Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/')))
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var lengthBuffer = new byte[sizeof(long)];
        var readBuffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            foreach (var (fullPath, relativePath) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
                hash.AppendData(FingerprintSeparator);

                await using var stream = File.OpenRead(fullPath);
                BitConverter.TryWriteBytes(lengthBuffer, stream.Length);
                hash.AppendData(lengthBuffer);

                while (true)
                {
                    var bytesRead = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0) break;
                    hash.AppendData(readBuffer.AsSpan(0, bytesRead));
                }

                hash.AppendData(FingerprintSeparator);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    /// <summary>
    /// Enumerates package contents while following valid links and rejecting broken targets or ancestor cycles.
    /// </summary>
    internal static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        var activeAncestors = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return EnumerateSourceFilesCore(directory, activeAncestors);
    }

    /// <summary>
    /// Recursively walks directory entries while tracking active ancestor canonical paths to guard against link cycles.
    /// </summary>
    private static IEnumerable<string> EnumerateSourceFilesCore(string directory, HashSet<string> activeAncestors)
    {
        var resolvedDirectory = ResolveCanonicalDirectoryPath(directory);
        if (!Directory.Exists(resolvedDirectory))
        {
            throw new ModValidationException("BrokenSymbolicLink", $"Directory '{directory}' or its link target does not exist.");
        }

        if (!activeAncestors.Add(resolvedDirectory))
        {
            throw new ModValidationException("SymbolicLinkCycle", $"Directory link cycle detected at '{directory}'.");
        }

        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var attributes = File.GetAttributes(path);
                var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (isReparsePoint && !Directory.Exists(path))
                    {
                        throw new ModValidationException("BrokenSymbolicLink", $"Broken symbolic link '{path}'.");
                    }

                    foreach (var file in EnumerateSourceFilesCore(path, activeAncestors))
                    {
                        yield return file;
                    }
                }
                else
                {
                    if (isReparsePoint && !File.Exists(path))
                    {
                        throw new ModValidationException("BrokenSymbolicLink", $"Broken symbolic link '{path}'.");
                    }

                    yield return path;
                }
            }
        }
        finally
        {
            activeAncestors.Remove(resolvedDirectory);
        }
    }

    /// <summary>
    /// Resolves a directory or reparse point to its canonical target path without trailing separators.
    /// </summary>
    private static string ResolveCanonicalDirectoryPath(string directory)
    {
        var resolved = Directory.ResolveLinkTarget(directory, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(directory);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
    }

    /// <summary>
    /// Validates the complete staging tree before moving it to a stable, unique runtime location.
    /// </summary>
    private ModInstallation Publish(string staging, string installationId, CancellationToken cancellationToken)
    {
        var manifest = ModManifestFile.Read(staging);
        ValidateDeclaredFiles(staging, manifest);
        cancellationToken.ThrowIfCancellationRequested();

        var modDirectory = Path.Combine(RootDirectory, "packages", manifest.Id);
        Directory.CreateDirectory(modDirectory);
        var version = ModCompatibility.ParseVersion(manifest.Version).ToNormalizedString();
        var finalDirectory = Path.Combine(modDirectory, $"{version}-{installationId}");
        Directory.Move(staging, finalDirectory);

        return new ModInstallation
        {
            InstallationId = installationId,
            Directory = finalDirectory,
            Manifest = manifest
        };
    }

    /// <summary>
    /// Checks every declared asset across all variants and package metadata before publication.
    /// </summary>
    private static void ValidateDeclaredFiles(string directory, ModManifest manifest)
    {
        var entryFiles = manifest.Entry switch
        {
            UniversalModEntry universal => universal.Assemblies,
            PlatformModEntry platform => platform.Variants.SelectMany(variant => variant.Assemblies),
            _ => []
        };

        var licenseFiles = manifest.License is { } license
            ? (license.File is { } licenseFile ? license.ThirdPartyFiles.Prepend(licenseFile) : license.ThirdPartyFiles)
            : [];

        IEnumerable<string> iconFiles = manifest.Icon is { } icon ? [icon] : [];

        foreach (var file in entryFiles.Concat(manifest.Readme.Values).Concat(iconFiles).Concat(licenseFiles))
        {
            if (!File.Exists(PackagePath.Resolve(directory, file)))
            {
                throw new ModValidationException("MissingAsset", $"Declared asset '{file}' does not exist.");
            }
        }
    }

    /// <summary>
    /// Removes only the supplied owned directory and tolerates locks or permission failures.
    /// </summary>
    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
