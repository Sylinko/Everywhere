using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Everywhere.Plugin.Packaging;
using ZLinq;

namespace Everywhere.Plugin.Hosting.Loading;

/// <summary>
/// Holds inspected managed metadata without loading an assembly or retaining collectible reflection objects.
/// </summary>
internal sealed class AssemblyArtifact
{
    /// <summary>
    /// Gets the absolute path whose bytes were inspected.
    /// </summary>
    internal string Path { get; }

    /// <summary>
    /// Gets the CLR identity from the PE assembly definition.
    /// </summary>
    internal AssemblyName Identity { get; }

    /// <summary>
    /// Gets the ordinary static assembly references.
    /// </summary>
    internal IReadOnlyList<AssemblyName> References { get; }

    /// <summary>
    /// Gets the exact inspected-content fingerprint used for replacement and load verification.
    /// </summary>
    internal string Fingerprint { get; }

    /// <summary>
    /// Gets resource satellites indexed by metadata culture. They share this assembly's collectible owner.
    /// </summary>
    internal Dictionary<string, AssemblyArtifact> Satellites { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets metadata type names and their cumulative RequiresPlugin conditions.
    /// </summary>
    internal IReadOnlyDictionary<string, string[]> TypeRequirements { get; }

    /// <summary>
    /// Gets plugin IDs named by any type or method annotation, including declarations not materialized at runtime.
    /// </summary>
    internal IReadOnlySet<string> DeclaredRequirements { get; }

    /// <summary>
    /// Gets metadata type names that satisfy the structural entrypoint requirements.
    /// </summary>
    internal IReadOnlySet<string> EntryCandidates { get; }

    /// <summary>
    /// Reads a runnable managed assembly without executing any code.
    /// </summary>
    internal AssemblyArtifact(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(Path);
        Fingerprint = Convert.ToHexString(SHA256.HashData(bytes));

        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
        {
            throw new PluginValidationException("NotManagedAssembly", $"'{Path}' is not a managed assembly.");
        }

        var reader = pe.GetMetadataReader();
        if (!reader.IsAssembly)
        {
            throw new PluginValidationException("NotManagedAssembly", $"'{Path}' is not an assembly manifest.");
        }

        var definition = reader.GetAssemblyDefinition();
        Identity = new AssemblyName
        {
            Name = reader.GetString(definition.Name),
            Version = definition.Version,
            CultureName = reader.GetString(definition.Culture)
        };
        Identity.SetPublicKey(reader.GetBlobBytes(definition.PublicKey));
        References = reader.AssemblyReferences.AsValueEnumerable().Select(handle => ReadReference(reader, handle)).ToArray();

        if (definition.GetCustomAttributes()
            .AsValueEnumerable()
            .Any(handle => GetAttributeName(reader, handle) == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute"))
        {
            throw new PluginValidationException("ReferenceAssembly", $"'{Path}' is a compile-time reference assembly.");
        }

        var requirements = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var declaredRequirements = new HashSet<string>(StringComparer.Ordinal);
        var entryCandidates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var conditions = new List<string>();
            var current = handle;

            while (!current.IsNil)
            {
                var currentType = reader.GetTypeDefinition(current);
                conditions.AddRange(ReadRequirements(reader, currentType.GetCustomAttributes()));
                current = currentType.GetDeclaringType();
            }

            requirements.Add(GetTypeName(reader, handle), conditions.AsValueEnumerable().Distinct(StringComparer.Ordinal).ToArray());
            declaredRequirements.UnionWith(conditions);

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                declaredRequirements.UnionWith(ReadRequirements(reader, method.GetCustomAttributes()));

                if (reader.GetString(method.Name) != ".ctor" ||
                    (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
                {
                    continue;
                }

                var signature = reader.GetBlobReader(method.Signature);
                var header = signature.ReadSignatureHeader();
                if (header.IsGeneric) signature.ReadCompressedInteger();
                var parameterCount = signature.ReadCompressedInteger();

                if (parameterCount == 0 &&
                    type.GetGenericParameters().Count == 0 &&
                    (type.Attributes & TypeAttributes.Abstract) == 0 &&
                    (type.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public)
                {
                    entryCandidates.Add(GetTypeName(reader, handle));
                }
            }
        }

        TypeRequirements = requirements;
        DeclaredRequirements = declaredRequirements;
        EntryCandidates = entryCandidates;
    }

    /// <summary>
    /// Compares code and resource contents when sharing or replacing an owner.
    /// </summary>
    internal bool HasSameContent(AssemblyArtifact other)
    {
        return Fingerprint == other.Fingerprint &&
            Satellites.Count == other.Satellites.Count &&
            Satellites
                .AsValueEnumerable()
                .All(pair => other.Satellites.TryGetValue(pair.Key, out var satellite) && satellite.Fingerprint == pair.Value.Fingerprint);
    }

    /// <summary>
    /// Reads exactly the inspected file contents before loading, detecting replacement after preflight.
    /// </summary>
    internal byte[] ReadVerifiedBytes()
    {
        var bytes = File.ReadAllBytes(Path);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != Fingerprint)
        {
            throw new PluginValidationException("ArtifactChanged", $"'{Path}' changed after preflight.");
        }

        return bytes;
    }

    /// <summary>
    /// Applies CLR-compatible name, culture, token and minimum assembly-version requirements.
    /// </summary>
    internal static bool CanSatisfy(AssemblyName selected, AssemblyName requested)
    {
        return string.Equals(selected.Name, requested.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(selected.CultureName ?? string.Empty, requested.CultureName ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            (requested.GetPublicKeyToken() is not { } token || (selected.GetPublicKeyToken() ?? []).SequenceEqual(token)) &&
            (requested.Version is null || selected.Version >= requested.Version);
    }

    /// <summary>
    /// Reconstructs a reference identity, including whether metadata carries a full key or a token.
    /// </summary>
    private static AssemblyName ReadReference(MetadataReader reader, AssemblyReferenceHandle handle)
    {
        var reference = reader.GetAssemblyReference(handle);
        var identity = new AssemblyName
        {
            Name = reader.GetString(reference.Name),
            Version = reference.Version,
            CultureName = reader.GetString(reference.Culture)
        };
        var key = reader.GetBlobBytes(reference.PublicKeyOrToken);

        if ((reference.Flags & AssemblyFlags.PublicKey) != 0)
        {
            identity.SetPublicKey(key);
        }
        else
        {
            identity.SetPublicKeyToken(key);
        }

        return identity;
    }

    /// <summary>
    /// Reads fixed string arguments directly from custom-attribute blobs.
    /// </summary>
    private static IEnumerable<string> ReadRequirements(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            if (GetAttributeName(reader, handle) != "Everywhere.Plugin.Abstractions.RequiresPluginAttribute") continue;

            var attribute = reader.GetCustomAttribute(handle);
            var blob = reader.GetBlobReader(attribute.Value);

            if (blob.ReadUInt16() != 1 || blob.ReadSerializedString() is not { } pluginId)
            {
                throw new PluginValidationException("InvalidRequirement", "Malformed RequiresPlugin attribute.");
            }

            yield return pluginId;
        }
    }

    /// <summary>
    /// Resolves an attribute's declaring type without constructing its runtime Type.
    /// </summary>
    private static string GetAttributeName(MetadataReader reader, CustomAttributeHandle handle)
    {
        var constructor = reader.GetCustomAttribute(handle).Constructor;
        var type = constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default
        };

        if (type.Kind == HandleKind.TypeReference)
        {
            var reference = reader.GetTypeReference((TypeReferenceHandle)type);
            return $"{reader.GetString(reference.Namespace)}.{reader.GetString(reference.Name)}";
        }

        return type.Kind == HandleKind.TypeDefinition ? GetTypeName(reader, (TypeDefinitionHandle)type) : string.Empty;
    }

    /// <summary>
    /// Builds reflection-compatible nested type names solely from metadata.
    /// </summary>
    private static string GetTypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var parent = type.GetDeclaringType();
        var name = reader.GetString(type.Name);

        if (!parent.IsNil)
        {
            return $"{GetTypeName(reader, parent)}+{name}";
        }

        var space = reader.GetString(type.Namespace);
        return space.Length == 0 ? name : $"{space}.{name}";
    }
}