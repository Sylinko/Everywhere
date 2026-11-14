using AngleSharp.Html.Parser;

namespace Everywhere.Chat.Links;

public sealed record WebPreviewMetadata(string? Title, string? Description, Uri? Cover, Uri? Icon)
{
    private const int MaxHtmlBytes = 512 * 1024;

    public static async Task<WebPreviewMetadata> ReadAsync(Uri page, HttpContent content, CancellationToken cancellationToken)
    {
        // A standalone parser has no resource loader or scripting engine.
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        // The limit bounds reads, not total page size. Stop after the head or treat the limit as EOF.
        await using var prefix = new HtmlPrefixStream(input, MaxHtmlBytes, cancellationToken);
        var head = await parser.ParseHeadAsync(prefix, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return new WebPreviewMetadata(null, null, null, null);
        }

        using var document = head.Owner;
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in head.QuerySelectorAll("meta[content]"))
        {
            var key = element.GetAttribute("property") ?? element.GetAttribute("name");
            var value = element.GetAttribute("content");
            if (key is not null && !string.IsNullOrWhiteSpace(value))
            {
                metadata.TryAdd(key.Trim(), value);
            }
        }

        var baseUri = Resolve(page, head.QuerySelector("base[href]")?.GetAttribute("href")) ?? page;
        var cover = Resolve(baseUri, Find("og:image", "og:image:url", "og:image:secure_url", "twitter:image", "twitter:image:src"));
        Uri? icon = null;
        foreach (var element in head.QuerySelectorAll("link[rel][href]"))
        {
            var rel = element.GetAttribute("rel") ?? string.Empty;
            if (!rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("icon", StringComparer.OrdinalIgnoreCase) ||
                string.Equals(element.GetAttribute("type"), "image/svg+xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            icon = Resolve(baseUri, element.GetAttribute("href"));
            if (icon is not null && !icon.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            icon = null;
        }

        return new WebPreviewMetadata(
            CleanText(Find("og:title", "twitter:title") ?? head.QuerySelector("title")?.TextContent, 240),
            CleanText(Find("og:description", "twitter:description", "description"), 600),
            cover,
            icon);

        string? Find(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (metadata.TryGetValue(key, out var value))
                {
                    return value;
                }
            }

            return null;
        }
    }

    private static Uri? Resolve(Uri page, string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(page, value.Trim(), out var uri) &&
            LinkPreviewTransport.IsAllowed(uri) ?
                uri :
                null;
    }

    private static string? CleanText(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var output = new System.Text.StringBuilder(Math.Min(value.Length, limit));
        var needsSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                needsSpace = output.Length > 0;
                continue;
            }

            if (char.IsControl(character) ||
                character is '\u061c' or '\u200e' or '\u200f' or >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069')
            {
                continue;
            }

            if (needsSpace)
            {
                output.Append(' ');
                needsSpace = false;
            }

            output.Append(character);
            if (output.Length >= limit)
            {
                if (char.IsHighSurrogate(character))
                {
                    output.Length--;
                }

                output.Append('…');
                break;
            }
        }

        return output.Length == 0 ? null : output.ToString();
    }

    private sealed class HtmlPrefixStream(Stream input, int limit, CancellationToken requestToken) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private int _remaining = limit;

        public override int Read(byte[] buffer, int offset, int count)
        {
            // AngleSharp can read synchronously inside a token. Parsing runs off the UI thread,
            // and the request budget still applies to these reads.
            return ReadAsync(buffer.AsMemory(offset, count), requestToken).AsTask().GetAwaiter().GetResult();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0)
            {
                return 0;
            }

            // All parser reads belong to this request, including the initial encoding sniff with no token.
            var count = await input.ReadAsync(buffer[..Math.Min(buffer.Length, _remaining)], requestToken).ConfigureAwait(false);
            _remaining -= count;
            return count;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}