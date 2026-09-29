using System.Security.Cryptography;
using System.Text;
using Everywhere.Common;
using Everywhere.Utilities;

namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;

/// <summary>
/// Holds an immutable raw-byte and decoded-text snapshot used by planning and conflict checks.
/// </summary>
public sealed class PatchTextFileSnapshot
{
    private static readonly byte[][] KnownPreambles =
    [
        [0xFF, 0xFE, 0x00, 0x00],
        [0x00, 0x00, 0xFE, 0xFF],
        [0xEF, 0xBB, 0xBF],
        [0xFF, 0xFE],
        [0xFE, 0xFF],
        [0x84, 0x31, 0x95, 0x33]
    ];

    private PatchTextFileSnapshot(
        string path,
        bool exists,
        byte[] fingerprint,
        FileAttributes attributes,
        Encoding encoding,
        byte[] preamble,
        string content,
        IReadOnlyList<PatchSourceLine> lines,
        string defaultLineEnding)
    {
        Path = path;
        Exists = exists;
        Fingerprint = fingerprint;
        Attributes = attributes;
        Encoding = encoding;
        Preamble = preamble;
        Content = content;
        Lines = lines;
        DefaultLineEnding = defaultLineEnding;
    }

    public string Path { get; }

    public bool Exists { get; }

    public byte[] Fingerprint { get; }

    public FileAttributes Attributes { get; }

    public Encoding Encoding { get; }

    public byte[] Preamble { get; }

    public string Content { get; }

    public IReadOnlyList<PatchSourceLine> Lines { get; }

    public string DefaultLineEnding { get; }

    public bool EndsWithLineEnding => Lines.Count > 0 && Lines[^1].LineEnding.Length > 0;

    /// <summary>
    /// Reads and decodes an existing local text file while retaining its raw fingerprint and format.
    /// </summary>
    /// <param name="path">The canonical local file path.</param>
    /// <param name="maxBytes">The maximum number of bytes that may be loaded.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>An immutable file snapshot.</returns>
    public static async ValueTask<PatchTextFileSnapshot> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        var (bytes, attributes) = await ReadBytesAsync(path, maxBytes, cancellationToken);
        var encoding = await DetectEncodingAsync(bytes, cancellationToken) ??
            throw new PatchPlanException($"The target file '{path}' is not recognized as text and cannot be patched.");
        var preamble = FindPreamble(bytes);
        var strictEncoding = CreateStrictEncoding(encoding);
        var content = Decode(bytes.AsMemory(preamble.Length), strictEncoding, cancellationToken);
        var lines = ParseLines(content);

        return new PatchTextFileSnapshot(
            path,
            exists: true,
            SHA256.HashData(bytes),
            attributes,
            strictEncoding,
            preamble,
            content,
            lines,
            DetermineDefaultLineEnding(lines));
    }

    /// <summary>
    /// Creates the UTF-8, no-BOM baseline used for a new file operation.
    /// </summary>
    public static PatchTextFileSnapshot CreateNew(string path)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        return new PatchTextFileSnapshot(
            path,
            exists: false,
            SHA256.HashData([]),
            FileAttributes.Normal,
            encoding,
            [],
            string.Empty,
            [],
            Environment.NewLine);
    }

    /// <summary>
    /// Encodes new content with the snapshot's original encoding and preamble policy.
    /// </summary>
    public byte[] Encode(string content)
    {
        var encoded = Encoding.GetBytes(content);
        if (Preamble.Length == 0) return encoded;

        var result = new byte[Preamble.Length + encoded.Length];
        Preamble.CopyTo(result, 0);
        encoded.CopyTo(result, Preamble.Length);
        return result;
    }

    /// <summary>
    /// Compares raw bytes against the immutable snapshot fingerprint.
    /// </summary>
    public bool HasSameFingerprint(ReadOnlySpan<byte> bytes) =>
        CryptographicOperations.FixedTimeEquals(Fingerprint, SHA256.HashData(bytes));

    /// <summary>
    /// Splits decoded text into logical lines while retaining each line's original terminator.
    /// </summary>
    public static IReadOnlyList<PatchSourceLine> ParseLines(string content)
    {
        if (content.Length == 0) return [];

        var lines = new List<PatchSourceLine>();
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] is not ('\r' or '\n')) continue;

            var lineEndingLength = content[index] == '\r' && index + 1 < content.Length && content[index + 1] == '\n' ? 2 : 1;
            lines.Add(new PatchSourceLine(content[start..index], content.Substring(index, lineEndingLength)));
            index += lineEndingLength - 1;
            start = index + 1;
        }

        if (start < content.Length)
        {
            lines.Add(new PatchSourceLine(content[start..], string.Empty));
        }

        return lines;
    }

    /// <summary>
    /// Reassembles logical lines and their terminators into decoded text.
    /// </summary>
    public static string RenderLines(IReadOnlyList<PatchSourceLine> lines) =>
        string.Concat(lines.Select(static line => line.Text + line.LineEnding));

    private static async ValueTask<(byte[] Bytes, FileAttributes Attributes)> ReadBytesAsync(
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        PathUtilities.EntryInspection entry;
        try
        {
            entry = PathUtilities.InspectEntry(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PatchPlanException($"The target file '{path}' does not exist.");
        }

        if (entry.Kind is PathUtilities.EntryKind.Missing)
        {
            throw new PatchPlanException($"The target file '{path}' does not exist.");
        }

        if (entry.Kind is PathUtilities.EntryKind.Link)
        {
            throw new PatchPlanException(
                $"The resolved target file '{path}' is still a link to '{entry.RawLinkTarget}' and cannot be patched without a new resolution.");
        }

        if (entry.Kind is PathUtilities.EntryKind.UnsupportedReparsePoint)
        {
            throw new PatchPlanException(
                $"The resolved target file '{path}' is an unsupported reparse point and cannot be patched.");
        }

        if (entry.Attributes.HasFlag(FileAttributes.Directory))
        {
            throw new PatchPlanException($"The target path '{path}' is a directory, not a text file.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        if (stream.Length > maxBytes || stream.Length > int.MaxValue)
        {
            throw new PatchPlanException($"The target file '{path}' exceeds the patch size limit of {maxBytes} bytes.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
            if (read == 0) break;
            offset += read;
        }

        if (offset != bytes.Length)
        {
            Array.Resize(ref bytes, offset);
        }

        return (bytes, entry.Attributes);
    }

    private static async ValueTask<Encoding?> DetectEncodingAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        return await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken);
    }

    private static string Decode(ReadOnlyMemory<byte> bytes, Encoding encoding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return encoding.GetString(bytes.Span);
        }
        catch (DecoderFallbackException ex)
        {
            throw new PatchPlanException($"The target file encoding could not decode the complete file: {ex.Message}");
        }
    }

    private static Encoding CreateStrictEncoding(Encoding encoding)
    {
        var clone = (Encoding)encoding.Clone();
        clone.DecoderFallback = DecoderFallback.ExceptionFallback;
        clone.EncoderFallback = EncoderFallback.ExceptionFallback;
        return clone;
    }

    private static byte[] FindPreamble(ReadOnlySpan<byte> bytes)
    {
        foreach (var preamble in KnownPreambles)
        {
            if (bytes.StartsWith(preamble)) return preamble;
        }

        return [];
    }

    private static string DetermineDefaultLineEnding(IReadOnlyList<PatchSourceLine> lines)
    {
        var counts = lines
            .AsValueEnumerable()
            .Where(static line => line.LineEnding.Length > 0)
            .GroupBy(static line => line.LineEnding, StringComparer.Ordinal)
            .Select(static group => (LineEnding: group.Key, Count: group.Count()))
            .OrderByDescending(static item => item.Count)
            .ThenBy(static item => item.LineEnding, StringComparer.Ordinal)
            .FirstOrDefault();
        return counts.Count > 0 ? counts.LineEnding : Environment.NewLine;
    }
}

/// <summary>
/// Represents one decoded line and its exact line-ending sequence.
/// </summary>
public readonly record struct PatchSourceLine(string Text, string LineEnding);