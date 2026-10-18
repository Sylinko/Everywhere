using System.IO.Compression;

using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Hosting.Installation;

/// <summary>
/// Imports complete ZIP packages into distinct installation directories without mutating active files.
/// The application owns registration, desired selection and the decision that an installation may be retired.
/// </summary>
public sealed class PluginPackageStore(string rootDirectory, string dataDirectory, string stagingDirectory)
{
    /// <summary>
    /// Gets the package-store root beneath the host's writable data directory.
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
    public async Task<PluginInstallation> ImportAsync(Stream package, CancellationToken cancellationToken = default)
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
                        throw new PluginValidationException("DuplicatePackagePath", $"Duplicate archive path '{path}'.");
                    }

                    // Unix symlinks would change the meaning of later paths during extraction.
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    {
                        throw new PluginValidationException("UnsupportedSymbolicLink", $"Archive symlink '{path}' is not supported.");
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
    /// </summary>
    public async Task<PluginInstallation> ImportDirectoryAsync(string source, CancellationToken cancellationToken = default)
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
    /// Enumerates package contents without following links outside a user-supplied directory.
    /// </summary>
    internal static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new PluginValidationException("UnsupportedSymbolicLink", $"Package link '{directory}' is not supported.");
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PluginValidationException("UnsupportedSymbolicLink", $"Package link '{path}' is not supported.");
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var file in EnumerateSourceFiles(path))
                {
                    yield return file;
                }
            }
            else
            {
                yield return path;
            }
        }
    }

    /// <summary>
    /// Validates the complete staging tree before moving it to a stable, unique runtime location.
    /// </summary>
    private PluginInstallation Publish(string staging, string installationId, CancellationToken cancellationToken)
    {
        var manifest = PluginManifestFile.Read(staging);
        ValidateDeclaredFiles(staging, manifest);
        cancellationToken.ThrowIfCancellationRequested();

        var pluginDirectory = Path.Combine(RootDirectory, "packages", manifest.Id);
        Directory.CreateDirectory(pluginDirectory);
        var version = PluginCompatibility.ParseVersion(manifest.Version).ToNormalizedString();
        var finalDirectory = Path.Combine(pluginDirectory, $"{version}-{installationId}");
        Directory.Move(staging, finalDirectory);

        return new PluginInstallation
        {
            InstallationId = installationId,
            Directory = finalDirectory,
            Manifest = manifest
        };
    }

    /// <summary>
    /// Gets the stable writable data directory for a validated installation's plugin ID.
    /// </summary>
    public string GetDataDirectory(PluginInstallation installation)
    {
        PluginManifestFile.Validate(installation.Manifest);
        var path = Path.Combine(DataDirectory, installation.Manifest.Id);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Tries to delete an installation only after the caller has released all users and selection references.
    /// Returns false for filesystem failures so the host can defer deletion without undoing activation.
    /// </summary>
    public bool TryRemove(PluginInstallation installation)
    {
        var root = Path.GetFullPath(Path.Combine(RootDirectory, "packages")) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(installation.Directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!path.StartsWith(root, comparison))
        {
            throw new PluginValidationException("InvalidInstallationPath", "The installation is outside this package store.");
        }

        return TryDeleteDirectory(path);
    }

    /// <summary>
    /// Checks every declared variant before publication, including foreign platforms that will not be activated.
    /// </summary>
    private static void ValidateDeclaredFiles(string directory, PluginManifest manifest)
    {
        var files = manifest.Entry switch
        {
            UniversalPluginEntry universal => universal.Assemblies.Concat(universal.NativeLibraries).Concat(universal.ContentFiles),
            PlatformPluginEntry platform => platform.Variants.SelectMany(variant => variant.Assemblies.Concat(variant.NativeLibraries).Concat(variant.ContentFiles)),
            _ => []
        };

        foreach (var file in files.Concat(manifest.Readme.Values))
        {
            if (!File.Exists(PackagePath.Resolve(directory, file)))
            {
                throw new PluginValidationException("MissingAsset", $"Declared asset '{file}' does not exist.");
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