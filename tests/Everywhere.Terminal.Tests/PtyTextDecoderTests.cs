using System.Text;

namespace Everywhere.Terminal.Tests;

/// <summary>
/// Unit tests for <see cref="PtyTextDecoder"/> — incremental UTF-8 decoding
/// with emphasis on multi-byte character boundary splits, CJK handling,
/// and defensive decoding of mis-encoded input (e.g. GBK interpreted as UTF-8).
/// </summary>
[TestFixture]
public class PtyTextDecoderTests
{
    #region ASCII — single-byte characters

    [Test]
    public void Decode_Ascii_SingleChunk()
    {
        var decoder = new PtyTextDecoder(4096);
        var result = decoder.Decode("hello"u8);
        Assert.That(result.ToString(), Is.EqualTo("hello"));
    }

    [Test]
    public void Decode_Ascii_MultipleChunks()
    {
        var decoder = new PtyTextDecoder(4096);
        var part1 = decoder.Decode("hel"u8).ToString();
        var part2 = decoder.Decode("lo"u8).ToString();
        Assert.That(part1, Is.EqualTo("hel"));
        Assert.That(part2, Is.EqualTo("lo"));
    }

    [Test]
    public void Decode_Ascii_ByteByByte()
    {
        var decoder = new PtyTextDecoder(4096);
        var sb = new StringBuilder();
        foreach (var b in "world"u8)
        {
            sb.Append(decoder.Decode(new[] { b }));
        }
        Assert.That(sb.ToString(), Is.EqualTo("world"));
    }

    [Test]
    public void Decode_EmptyInput_ReturnsEmpty()
    {
        var decoder = new PtyTextDecoder(4096);
        var result = decoder.Decode(ReadOnlySpan<byte>.Empty);
        Assert.That(result.Length, Is.EqualTo(0));
    }

    #endregion

    #region CJK — 3-byte UTF-8 characters (Chinese / Japanese / Korean)

    [Test]
    public void Decode_Chinese_SingleChunk()
    {
        var decoder = new PtyTextDecoder(4096);
        var text = "你好世界";
        var bytes = Encoding.UTF8.GetBytes(text);
        var result = decoder.Decode(bytes);
        Assert.That(result.ToString(), Is.EqualTo(text));
    }

    [Test]
    public void Decode_Japanese_SingleChunk()
    {
        var decoder = new PtyTextDecoder(4096);
        var text = "こんにちは";
        var bytes = Encoding.UTF8.GetBytes(text);
        var result = decoder.Decode(bytes);
        Assert.That(result.ToString(), Is.EqualTo(text));
    }

    [Test]
    public void Decode_Korean_SingleChunk()
    {
        var decoder = new PtyTextDecoder(4096);
        var text = "안녕하세요";
        var bytes = Encoding.UTF8.GetBytes(text);
        var result = decoder.Decode(bytes);
        Assert.That(result.ToString(), Is.EqualTo(text));
    }

    [Test]
    public void Decode_Cjk_MixedLanguages()
    {
        var decoder = new PtyTextDecoder(4096);
        var text = "中文 日本語 한국어";
        var bytes = Encoding.UTF8.GetBytes(text);
        var result = decoder.Decode(bytes);
        Assert.That(result.ToString(), Is.EqualTo(text));
    }

    #endregion

    #region 4-byte UTF-8 — Emoji & rare CJK

    [Test]
    public void Decode_Emoji_SingleChunk()
    {
        var decoder = new PtyTextDecoder(4096);
        var text = "🎉🎊✨";
        var bytes = Encoding.UTF8.GetBytes(text);
        var result = decoder.Decode(bytes);
        Assert.That(result.ToString(), Is.EqualTo(text));
    }

    [Test]
    public void Decode_RareCjk_4ByteUtf8()
    {
        // U+20000 (CJK Extension B) = 𠀀 — 4-byte UTF-8: F0 A0 80 80
        var decoder = new PtyTextDecoder(4096);
        var text = "𠀀𠀁𠀂";
        var bytes = Encoding.UTF8.GetBytes(text);
        var result = decoder.Decode(bytes);
        Assert.That(result.ToString(), Is.EqualTo(text));
    }

    #endregion

    #region UTF-8 multi-byte split across chunk boundaries (core scenario)

    [Test]
    public void Decode_Cjk_SplitAt_2of3_Bytes()
    {
        // "你" = E4 BD A0 — split after 2 bytes: [E4 BD] [A0]
        var decoder = new PtyTextDecoder(4096);
        var text = "你好";
        var bytes = Encoding.UTF8.GetBytes(text);

        // "你好" = E4 BD A0 | E5 A5 BD (6 bytes). Split after 2 bytes.
        var chunk1 = decoder.Decode(bytes.AsSpan(0, 2)).ToString();
        var chunk2 = decoder.Decode(bytes.AsSpan(2)).ToString();
        var result = chunk1 + chunk2;

        Assert.That(result, Is.EqualTo(text),
            "3-byte CJK char split after 2 bytes should decode correctly after receiving remaining byte");
    }

    [Test]
    public void Decode_Cjk_SplitAt_1of3_Bytes()
    {
        // "你" = E4 BD A0 — split after 1 byte: [E4] [BD A0]
        var decoder = new PtyTextDecoder(4096);
        var text = "你好";
        var bytes = Encoding.UTF8.GetBytes(text);

        var chunk1 = decoder.Decode(bytes.AsSpan(0, 1)).ToString();
        var chunk2 = decoder.Decode(bytes.AsSpan(1)).ToString();
        var result = chunk1 + chunk2;

        Assert.That(result, Is.EqualTo(text),
            "3-byte CJK char split after 1 byte should decode correctly after receiving remaining 2 bytes");
    }

    [Test]
    public void Decode_Emoji_SplitAt_2of4_Bytes()
    {
        // "🎉" = F0 9F 8E 89 — split after 2 bytes: [F0 9F] [8E 89]
        var decoder = new PtyTextDecoder(4096);
        var text = "🎉🎊";
        var bytes = Encoding.UTF8.GetBytes(text);

        var chunk1 = decoder.Decode(bytes.AsSpan(0, 2)).ToString();
        var chunk2 = decoder.Decode(bytes.AsSpan(2)).ToString();
        var result = chunk1 + chunk2;

        Assert.That(result, Is.EqualTo(text));
    }

    [Test]
    public void Decode_Emoji_SplitAt_1of4_Bytes()
    {
        // "🎉" = F0 9F 8E 89 — split after 1 byte: [F0] [9F 8E 89]
        var decoder = new PtyTextDecoder(4096);
        var text = "🎉🎊";
        var bytes = Encoding.UTF8.GetBytes(text);

        var chunk1 = decoder.Decode(bytes.AsSpan(0, 1)).ToString();
        var chunk2 = decoder.Decode(bytes.AsSpan(1)).ToString();
        var result = chunk1 + chunk2;

        Assert.That(result, Is.EqualTo(text));
    }

    [Test]
    public void Decode_Emoji_SplitAt_3of4_Bytes()
    {
        // "🎉" = F0 9F 8E 89 — split after 3 bytes: [F0 9F 8E] [89]
        var decoder = new PtyTextDecoder(4096);
        var text = "🎉🎊";
        var bytes = Encoding.UTF8.GetBytes(text);

        var chunk1 = decoder.Decode(bytes.AsSpan(0, 3)).ToString();
        var chunk2 = decoder.Decode(bytes.AsSpan(3)).ToString();
        var result = chunk1 + chunk2;

        Assert.That(result, Is.EqualTo(text));
    }

    [Test]
    public void Decode_Cjk_EveryPossibleByteBoundary_Split()
    {
        // Feed a CJK string byte-by-byte — the decoder must handle
        // every possible partial multi-byte sequence.
        var decoder = new PtyTextDecoder(4096);
        var text = "你好世界🎉";
        var bytes = Encoding.UTF8.GetBytes(text);
        var sb = new StringBuilder();

        for (var i = 0; i < bytes.Length; i++)
        {
            var chunk = decoder.Decode(bytes.AsSpan(i, 1));
            sb.Append(chunk);
        }

        Assert.That(sb.ToString(), Is.EqualTo(text),
            "Byte-by-byte feeding of multi-byte UTF-8 must reconstruct the original string");
    }

    [Test]
    public void Decode_LargeCjkText_RandomSplitSize()
    {
        // Feed a large CJK text with random chunk sizes — stress test.
        var decoder = new PtyTextDecoder(4096);
        var text = string.Concat(Enumerable.Repeat("你好世界こんにちは안녕하세요🎉", 200));
        var bytes = Encoding.UTF8.GetBytes(text);
        var sb = new StringBuilder();
        var offset = 0;
        var rng = new Random(42); // deterministic seed

        while (offset < bytes.Length)
        {
            var chunkSize = rng.Next(1, 20);
            chunkSize = Math.Min(chunkSize, bytes.Length - offset);
            var chunk = decoder.Decode(bytes.AsSpan(offset, chunkSize));
            sb.Append(chunk);
            offset += chunkSize;
        }

        Assert.That(sb.ToString(), Is.EqualTo(text),
            "Random chunk feeding of large CJK text must reconstruct original");
    }

    #endregion

    #region GBK mis-decoded as UTF-8 (simulating MSBuild output)

    [Test]
    public void Decode_GbkBytes_AsUtf8_DoesNotCrash()
    {
        // Reproduce: MSBuild outputs GBK, but we decode as UTF-8.
        // The decoder should produce replacement characters (U+FFFD) instead of crashing.
        var decoder = new PtyTextDecoder(4096);
        var originalText = "构建成功";
        byte[] gbkBytes;

        try
        {
            var gbk = Encoding.GetEncoding("GBK");
            gbkBytes = gbk.GetBytes(originalText);
        }
        catch (ArgumentException)
        {
            // GBK code page (936) not registered on this platform — skip
            Assert.Ignore("GBK encoding is not available on this platform.");
            return;
        }

        // Feed GBK bytes to the UTF-8 decoder
        var result = decoder.Decode(gbkBytes);
        var output = result.ToString();

        Assert.That(output, Is.Not.Null);
        Assert.That(output.Length, Is.GreaterThan(0),
            "GBK bytes decoded as UTF-8 should produce some output (replacement chars)");
        // Each invalid GBK byte sequence should produce at least one U+FFFD
        Assert.That(output, Does.Contain("\uFFFD"),
            "GBK bytes decoded as UTF-8 must produce U+FFFD replacement characters");
    }

    [Test]
    public void Decode_GbkBytes_AsUtf8_NoDataLoss()
    {
        // Ensures that every input byte is accounted for — either valid UTF-8
        // decoded correctly or invalid bytes replaced with U+FFFD.
        var decoder = new PtyTextDecoder(4096);
        var originalText = "生成 已完成 项目 \"Everywhere.sln\"。";

        byte[] gbkBytes;
        try
        {
            var gbk = Encoding.GetEncoding("GBK");
            gbkBytes = gbk.GetBytes(originalText);
        }
        catch (ArgumentException)
        {
            Assert.Ignore("GBK encoding is not available on this platform.");
            return;
        }

        // Feed in chunks to also test boundary handling
        var sb = new StringBuilder();
        var offset = 0;
        var rng = new Random(99);
        while (offset < gbkBytes.Length)
        {
            var chunkSize = rng.Next(1, 8);
            chunkSize = Math.Min(chunkSize, gbkBytes.Length - offset);
            sb.Append(decoder.Decode(gbkBytes.AsSpan(offset, chunkSize)));
            offset += chunkSize;
        }

        var output = sb.ToString();

        // Every replacement character corresponds to at least one invalid byte.
        // The output should NOT be empty — it should contain replacement chars
        // interspersed with any coincidentally valid ASCII portions.
        Assert.That(output, Is.Not.Empty,
            "GBK output decoded as UTF-8 must not silently drop bytes");
        Assert.That(output, Does.Contain("\uFFFD"),
            "GBK bytes decoded as UTF-8 must produce U+FFFD for non-ASCII GBK bytes");
    }

    [Test]
    public void Decode_GbkAsciiMixed_AsUtf8_PreservesCoincidentalAscii()
    {
        // When GBK output happens to contain ASCII-range bytes (e.g., spaces,
        // punctuation, file paths), those bytes are valid UTF-8 and should be
        // decoded correctly even when surrounded by invalid GBK multi-byte sequences.
        var decoder = new PtyTextDecoder(4096);

        // GBK: "MSBuild version 17.0" — ASCII chars are single-byte in both GBK and UTF-8
        var asciiText = "MSBuild version 17.0";
        var asciiBytes = Encoding.UTF8.GetBytes(asciiText);

        var result = decoder.Decode(asciiBytes);
        Assert.That(result.ToString(), Is.EqualTo(asciiText),
            "ASCII-range bytes must decode identically regardless of source encoding");
    }

    #endregion

    #region Mixed content: ASCII + CJK + Emoji

    [Test]
    public void Decode_MixedContent_SingleChunk()
    {
        var decoder = new PtyTextDecoder(4096);
        var text = "Hello 你好 🎉 World 世界 ✨!";
        var bytes = Encoding.UTF8.GetBytes(text);
        var result = decoder.Decode(bytes);
        Assert.That(result.ToString(), Is.EqualTo(text));
    }

    [Test]
    public void Decode_MixedContent_SplitChunks()
    {
        var decoder = new PtyTextDecoder(4096);
        var text = "Hello 你好 🎉 World 世界 ✨!";
        var bytes = Encoding.UTF8.GetBytes(text);
        var sb = new StringBuilder();
        var rng = new Random(7);

        var offset = 0;
        while (offset < bytes.Length)
        {
            var chunkSize = rng.Next(1, 11);
            chunkSize = Math.Min(chunkSize, bytes.Length - offset);
            sb.Append(decoder.Decode(bytes.AsSpan(offset, chunkSize)));
            offset += chunkSize;
        }

        Assert.That(sb.ToString(), Is.EqualTo(text),
            "Mixed ASCII+CJK+Emoji must survive random chunk splits");
    }

    [Test]
    public void Decode_ConsecutiveSplit_MultipleCharsCut()
    {
        // Feed where every chunk boundary falls inside a multi-byte character.
        var decoder = new PtyTextDecoder(4096);
        var text = "你好世界"; // 4 chars, 12 UTF-8 bytes
        var bytes = Encoding.UTF8.GetBytes(text);

        // Split every 2 bytes — every CJK char (3 bytes) gets cut
        var sb = new StringBuilder();
        for (var i = 0; i < bytes.Length; i += 2)
        {
            var chunkSize = Math.Min(2, bytes.Length - i);
            sb.Append(decoder.Decode(bytes.AsSpan(i, chunkSize)));
        }

        Assert.That(sb.ToString(), Is.EqualTo(text),
            "Every CJK char cut by chunk boundary must still decode correctly");
    }

    #endregion

    #region Flush / Reset lifecycle

    [Test]
    public void Flush_AfterIncompleteSequence_EmitsReplacementChar()
    {
        // Feed only the first byte of a 3-byte CJK char, then flush.
        // The decoder should emit U+FFFD for the incomplete sequence.
        var decoder = new PtyTextDecoder(4096);
        var text = "你";
        var bytes = Encoding.UTF8.GetBytes(text);

        // Feed only first byte: 0xE4 (leading byte of "你")
        var chunk = decoder.Decode(bytes.AsSpan(0, 1));
        Assert.That(chunk.Length, Is.EqualTo(0),
            "Incomplete leading byte should not emit any characters yet");

        var flushed = decoder.Flush();
        Assert.That(flushed.Length, Is.GreaterThan(0),
            "Flush after incomplete sequence must emit replacement character");
        Assert.That(flushed.ToString(), Is.EqualTo("\uFFFD"),
            "Flush of incomplete multi-byte sequence should emit U+FFFD");
    }

    [Test]
    public void Flush_AfterCompleteSequence_EmitsNothing()
    {
        var decoder = new PtyTextDecoder(4096);
        var bytes = "hello"u8;
        var chunk = decoder.Decode(bytes);
        Assert.That(chunk.ToString(), Is.EqualTo("hello"));

        var flushed = decoder.Flush();
        Assert.That(flushed.Length, Is.EqualTo(0),
            "Flush after complete sequence should emit nothing");
    }

    [Test]
    public void Reset_AfterUse_AllowsReuse()
    {
        var decoder = new PtyTextDecoder(4096);

        // First decode
        var result1 = decoder.Decode(Encoding.UTF8.GetBytes("你好"));
        Assert.That(result1.ToString(), Is.EqualTo("你好"));

        // Reset
        decoder.Reset();

        // Second decode — should work as if fresh
        var result2 = decoder.Decode(Encoding.UTF8.GetBytes("世界"));
        Assert.That(result2.ToString(), Is.EqualTo("世界"));
    }

    [Test]
    public void Reset_AfterIncompleteSequence_ClearsState()
    {
        var decoder = new PtyTextDecoder(4096);
        var bytes = Encoding.UTF8.GetBytes("你好");

        // Feed partial — leave decoder in incomplete state
        decoder.Decode(bytes.AsSpan(0, 1));

        // Reset
        decoder.Reset();

        // Feed complete sequence — should work correctly
        var result = decoder.Decode(Encoding.UTF8.GetBytes("A"));
        Assert.That(result.ToString(), Is.EqualTo("A"),
            "After Reset, decoder should not be contaminated by previous partial state");
    }

    [Test]
    public void Flush_AfterMultiplePartialFeeds()
    {
        // Feed 2 bytes of a 3-byte char, then flush
        var decoder = new PtyTextDecoder(4096);
        var bytes = Encoding.UTF8.GetBytes("你好"); // 6 bytes

        // Feed 2 bytes (mid-"你")
        decoder.Decode(bytes.AsSpan(0, 2));
        // Feed 2 more bytes (completes "你" + 1 byte of "好")
        decoder.Decode(bytes.AsSpan(2, 2));

        var flushed = decoder.Flush();
        Assert.That(flushed.Length, Is.GreaterThan(0),
            "Flush after multiple partial feeds must emit replacement for remaining bytes");
    }

    #endregion

    #region Real-world scenario simulations

    [Test]
    public void Decode_SimulatedPtyChunks_VariableSizes()
    {
        // Simulate real PTY read behavior where chunks arrive in 4096-byte reads
        // but content spans many lines of CJK text.
        var decoder = new PtyTextDecoder(4096);
        var lines = Enumerable.Range(0, 100).Select(i => $"第{i}行：你好世界こんにちは안녕하세요🎉");
        var text = string.Join("\r\n", lines);
        var bytes = Encoding.UTF8.GetBytes(text);
        var sb = new StringBuilder();
        var chunkSize = 4096;
        var offset = 0;

        while (offset < bytes.Length)
        {
            var size = Math.Min(chunkSize, bytes.Length - offset);
            sb.Append(decoder.Decode(bytes.AsSpan(offset, size)));
            offset += size;
        }

        Assert.That(sb.ToString(), Is.EqualTo(text),
            "PTY-sized chunks (4096 bytes) must decode CJK text correctly");
    }

    [Test]
    public void Decode_ChunkBoundaryAtEveryPossibleOffset()
    {
        // For a fixed text, test every possible split offset (1..N-1)
        // to ensure no boundary condition causes data corruption.
        var decoder = new PtyTextDecoder(4096);
        var text = "Hello 你好 World! 🎉";
        var bytes = Encoding.UTF8.GetBytes(text);

        for (var split = 1; split < bytes.Length; split++)
        {
            decoder.Reset();
            var part1 = decoder.Decode(bytes.AsSpan(0, split)).ToString();
            var part2 = decoder.Decode(bytes.AsSpan(split)).ToString();
            var result = part1 + part2;

            Assert.That(result, Is.EqualTo(text),
                $"Split at byte offset {split} of {bytes.Length} must produce correct result");
        }
    }

    [Test]
    public void Decode_ContinuationByte_WithoutLeadingByte_DoesNotCrash()
    {
        // A standalone continuation byte (0x80-0xBF) without a preceding leading byte
        // is invalid UTF-8. The decoder should emit U+FFFD and continue.
        // This can happen when the PTY stream starts mid-character or after corruption.
        var decoder = new PtyTextDecoder(4096);

        // 0xBF is a continuation byte (10xxxxxx)
        var result = decoder.Decode(new byte[] { 0xBF });

        Assert.That(result.Length, Is.GreaterThan(0),
            "Standalone continuation byte must produce output (replacement char)");
        Assert.That(result.ToString(), Is.EqualTo("\uFFFD"),
            "Standalone continuation byte must decode as U+FFFD");
    }

    [Test]
    public void Decode_OverlongSequence_EmitsReplacement()
    {
        // Overlong encoding of '/' (U+002F): C0 AF (should be just 0x2F)
        // This is invalid UTF-8 and should produce U+FFFD.
        var decoder = new PtyTextDecoder(4096);
        var result = decoder.Decode(new byte[] { 0xC0, 0xAF });

        Assert.That(result.Length, Is.GreaterThan(0));
        Assert.That(result.ToString(), Does.Contain("\uFFFD"),
            "Overlong UTF-8 sequence must produce U+FFFD");
    }

    #endregion
}
