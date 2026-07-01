using System.Buffers;
using System.Text;

namespace Everywhere.Utilities;

/// <summary>
/// Provides functionality to detect the encoding of a text stream.
/// It prioritizes BOM detection and then uses heuristics and statistical analysis
/// to guess the encoding for non-BOM files.
///
/// TODO: Current implementation is a workaround, and may not be perfect. Consider integrating a well-known library like Ude or CharDet in the future for more robust detection.
/// </summary>
public static class EncodingDetector
{
    private static readonly Encoding Gbk;
    private static readonly Encoding Big5;
    private static readonly Encoding ShiftJis;
    private static readonly Encoding Gb18030;
    private static readonly Encoding Windows1252;

    static EncodingDetector()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding("GBK");
        Big5 = Encoding.GetEncoding("Big5");
        ShiftJis = Encoding.GetEncoding("shift_jis");
        Gb18030 = Encoding.GetEncoding("GB18030");
        Windows1252 = Encoding.GetEncoding(1252);
    }

    /// <summary>
    /// Tries to detect the encoding of a stream by reading its initial bytes.
    /// Returns null if the stream is likely binary or the encoding cannot be determined.
    /// </summary>
    public static async Task<Encoding?> DetectEncodingAsync(Stream stream, int bufferSize = 4096, CancellationToken cancellationToken = default)
    {
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

        var originalPosition = stream.Position;
        stream.Seek(0, SeekOrigin.Begin);

        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            // ReadAtLeastAsync ensures we fill the buffer if possible, reducing partial read issues.
            var bytesRead = await stream.ReadAtLeastAsync(buffer.AsMemory(0, bufferSize), bufferSize, throwOnEndOfStream: false, cancellationToken: cancellationToken);

            if (bytesRead == 0) return new UTF8Encoding(false);

            var span = buffer.AsSpan(0, bytesRead);

            var bomEncoding = DetectBom(span);
            if (bomEncoding != null) return bomEncoding;

            return IsLikelyBinary(span) ? null : DetectEncodingWithoutBom(span);

        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            stream.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    private static Encoding? DetectBom(ReadOnlySpan<byte> buffer)
    {
        if (buffer.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8;
        if (buffer.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })) return Encoding.UTF32;
        if (buffer.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })) return new UTF32Encoding(true, true);
        if (buffer.StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode;
        if (buffer.StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode;
        if (buffer.StartsWith(new byte[] { 0x84, 0x31, 0x95, 0x33 })) return Gb18030;
        return null;
    }

    private static bool IsLikelyBinary(ReadOnlySpan<byte> buffer)
    {
        // Check for null bytes and other common control characters that rarely appear in text.
        // 0x00-0x08, 0x0B, 0x0C, 0x0E-0x1F are control chars.
        // 0x09 (TAB), 0x0A (LF), 0x0D (CR) are common text chars.
        
        const double threshold = 0.1;
        var checkLen = Math.Min(buffer.Length, 1000);
        var suspiciousCount = 0;
        
        for (var i = 0; i < checkLen; i++)
        {
            var b = buffer[i];
            if (b == 0x00 || 
               b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D)
            {
                suspiciousCount++;
            }
        }
        return (double)suspiciousCount / checkLen > threshold;
    }

    private static Encoding DetectEncodingWithoutBom(ReadOnlySpan<byte> buffer)
    {
        // State variables for parallel detection
        
        // UTF-8
        var utf8Invalid = false;
        var utf8HasMultibyte = false;
        var utf8Need = 0;

        // GB18030 (covers GBK)
        var gb18030Invalid = false;
        long gb18030Score = 0;
        var gb18030State = 0; // 0: Start, 1: 2nd byte, 2: 3rd byte, 3: 4th byte
        var gb18030Has4Byte = false;

        // Big5
        var big5Invalid = false;
        long big5Score = 0;
        var big5State = 0; // 0: Start, 1: 2nd byte

        // Shift-JIS
        var sjisInvalid = false;
        long sjisScore = 0;
        var sjisState = 0; // 0: Start, 1: 2nd byte

        foreach (byte b in buffer)
        {
            // --- UTF-8 Logic ---
            if (!utf8Invalid)
            {
                if (utf8Need == 0)
                {
                    if (b < 0x80) { /* ASCII */ }
                    else if ((b & 0xE0) == 0xC0) { utf8Need = 1; utf8HasMultibyte = true; }
                    else if ((b & 0xF0) == 0xE0) { utf8Need = 2; utf8HasMultibyte = true; }
                    else if ((b & 0xF8) == 0xF0) { utf8Need = 3; utf8HasMultibyte = true; }
                    else utf8Invalid = true;
                }
                else
                {
                    if ((b & 0xC0) == 0x80) utf8Need--;
                    else utf8Invalid = true;
                }
            }

            // --- GB18030 Logic ---
            if (!gb18030Invalid)
            {
                switch (gb18030State)
                {
                    case 0: // Expect 1st
                        switch (b)
                        {
                            case >= 0x81 and <= 0xFE:
                                gb18030State = 1;
                                break;
                            case >= 0x80:
                                gb18030Invalid = true; // High ASCII unused in start
                                break;
                        }
                        break;
                    case 1: // Expect 2nd
                        switch (b)
                        {
                            case >= 0x40 and <= 0xFE when b != 0x7F:
                                gb18030Score++; // Valid 2-byte sequence
                                gb18030State = 0;
                                break;
                            case >= 0x30 and <= 0x39:
                                gb18030State = 2; // Possible 4-byte sequence
                                break;
                            default:
                                gb18030Invalid = true;
                                break;
                        }
                        break;
                    case 2: // Expect 3rd
                        if (b is >= 0x81 and <= 0xFE) gb18030State = 3;
                        else gb18030Invalid = true;
                        break;
                    case 3: // Expect 4th
                        if (b is >= 0x30 and <= 0x39)
                        {
                            gb18030Score++;
                            gb18030Has4Byte = true;
                            gb18030State = 0;
                        }
                        else gb18030Invalid = true;
                        break;
                }
            }

            // --- Big5 Logic ---
            if (!big5Invalid)
            {
                if (big5State == 0)
                {
                    switch (b)
                    {
                        case >= 0x81 and <= 0xFE:
                            big5State = 1;
                            break;
                        case >= 0x80:
                            big5Invalid = true;
                            break;
                    }
                }
                else // State 1
                {
                    if (b is >= 0x40 and <= 0x7E || b is >= 0xA1 and <= 0xFE)
                    {
                        big5Score++;
                        big5State = 0;
                    }
                    else big5Invalid = true;
                }
            }

            // --- Shift-JIS Logic ---
            if (!sjisInvalid)
            {
                if (sjisState == 0)
                {
                    switch (b)
                    {
                        case >= 0xA1 and <= 0xDF:
                            // Half-width Katakana, valid 1-byte
                            sjisScore++;
                            break;
                        case >= 0x81 and <= 0x9F:
                        case >= 0xE0 and <= 0xFC:
                            sjisState = 1;
                            break;
                        case >= 0x80:
                            sjisInvalid = true;
                            break;
                    }
                }
                else // State 1
                {
                    if (b is >= 0x40 and <= 0x7E or >= 0x80 and <= 0xFC)
                    {
                        sjisScore++;
                        sjisState = 0;
                    }
                    else sjisInvalid = true;
                }
            }
        }

        // --- Decision Logic ---

        // 1. UTF-8 (Strict)
        // If strictly valid and contains multibyte chars, or is just ASCII, it's UTF-8.
        if (!utf8Invalid && (utf8HasMultibyte || (gb18030Score == 0 && big5Score == 0 && sjisScore == 0)))
        {
             return new UTF8Encoding(false);
        }

        // Candidates map for Multi-byte
        var candidates = new List<(Encoding Encoding, long Score)>();

        if (!gb18030Invalid) candidates.Add((Gb18030, gb18030Score));
        if (!big5Invalid) candidates.Add((Big5, big5Score));
        if (!sjisInvalid) candidates.Add((ShiftJis, sjisScore));
        
        if (candidates.Count == 0) return Windows1252;

        // Priority Logic:
        // If GB18030 has 4-byte sequence, it wins immediately.
        if (!gb18030Invalid && gb18030Has4Byte) return Gb18030;

        // Sort by score
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        // If score is 0, it means we only found ASCII or truncated sequences?
        return candidates[0].Score == 0 ? Windows1252 : candidates[0].Encoding;

    }
}
