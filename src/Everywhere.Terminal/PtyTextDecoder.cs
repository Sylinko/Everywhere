using System.Text;

namespace Everywhere.Terminal;

/// <summary>
/// Incrementally decodes UTF-8 text from PTY byte chunks.
/// </summary>
public sealed class PtyTextDecoder(int byteBufferSize)
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private char[] _charBuffer = new char[Encoding.UTF8.GetMaxCharCount(byteBufferSize)];

    public ReadOnlySpan<char> Decode(ReadOnlySpan<byte> bytes)
    {
        return Decode(bytes, flush: false);
    }

    public ReadOnlySpan<char> Flush()
    {
        return Decode(ReadOnlySpan<byte>.Empty, flush: true);
    }

    public void Reset()
    {
        _decoder.Reset();
    }

    private ReadOnlySpan<char> Decode(ReadOnlySpan<byte> bytes, bool flush)
    {
        EnsureCapacity(Encoding.UTF8.GetMaxCharCount(bytes.Length));

        _decoder.Convert(
            bytes,
            _charBuffer,
            flush,
            out var bytesUsed,
            out var charsUsed,
            out var completed);

        if (!completed || bytesUsed != bytes.Length)
        {
            EnsureCapacity(Encoding.UTF8.GetMaxCharCount(bytes.Length) * 2);
            _decoder.Convert(
                bytes[bytesUsed..],
                _charBuffer.AsSpan(charsUsed),
                flush,
                out _,
                out var additionalChars,
                out _);
            charsUsed += additionalChars;
        }

        return _charBuffer.AsSpan(0, charsUsed);
    }

    private void EnsureCapacity(int required)
    {
        if (_charBuffer.Length >= required) return;
        Array.Resize(ref _charBuffer, required);
    }
}
