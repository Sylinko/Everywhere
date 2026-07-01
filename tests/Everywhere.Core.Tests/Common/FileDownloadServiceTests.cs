using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Everywhere.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.Common;

public class FileDownloadServiceTests
{
    [Test]
    public async Task DownloadAsync_FallsBackToResponsiveSource()
    {
        var bytes = "hello runtime"u8.ToArray();
        using var temp = new TempFile();
        var service = CreateService(request =>
        {
            if (request.RequestUri!.Host == "bad.test")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
        });

        var result = await service.DownloadAsync(
            new FileDownloadRequest(
                temp.Path,
                [
                    new FileDownloadSource("https://bad.test/runtime.zip"),
                    new FileDownloadSource("https://good.test/runtime.zip")
                ],
                bytes.Length,
                Sha256(bytes)));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(temp.Path));
            Assert.That(File.ReadAllBytes(temp.Path), Is.EqualTo(bytes));
        });
    }

    [Test]
    public async Task DownloadAsync_ResumesPartialDownload()
    {
        var bytes = "partial runtime"u8.ToArray();
        using var temp = new TempFile();
        await File.WriteAllBytesAsync(temp.Path + ".part", bytes[..7]);
        var sawRange = false;

        var service = CreateService(request =>
        {
            sawRange = request.Headers.Range?.Ranges.Single().From == 7;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes[7..])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(7, bytes.Length - 1, bytes.Length);
            return response;
        });

        await service.DownloadAsync(
            new FileDownloadRequest(
                temp.Path,
                [new FileDownloadSource("https://good.test/runtime.zip")],
                bytes.Length,
                Sha256(bytes)));

        Assert.Multiple(() =>
        {
            Assert.That(sawRange, Is.True);
            Assert.That(File.ReadAllBytes(temp.Path), Is.EqualTo(bytes));
        });
    }

    [Test]
    public async Task DownloadAsync_RestartsWhenRangeIsIgnored()
    {
        var bytes = "restart runtime"u8.ToArray();
        using var temp = new TempFile();
        await File.WriteAllBytesAsync(temp.Path + ".part", bytes[..4]);
        var ignoredRange = false;

        var service = CreateService(request =>
        {
            if (request.Headers.Range is not null)
            {
                ignoredRange = true;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
        });

        await service.DownloadAsync(
            new FileDownloadRequest(
                temp.Path,
                [new FileDownloadSource("https://good.test/runtime.zip")],
                bytes.Length,
                Sha256(bytes)));

        Assert.Multiple(() =>
        {
            Assert.That(ignoredRange, Is.True);
            Assert.That(File.ReadAllBytes(temp.Path), Is.EqualTo(bytes));
        });
    }

    [Test]
    public async Task DownloadAsync_ReportsIndeterminateProgressWhenSizeIsUnknown()
    {
        var bytes = "unknown length runtime"u8.ToArray();
        using var temp = new TempFile();
        var progress = new RecordingProgress();
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(bytes)
        });

        await service.DownloadAsync(
            new FileDownloadRequest(
                temp.Path,
                [new FileDownloadSource("https://good.test/runtime.zip")],
                Sha256Digest: Sha256(bytes)),
            progress);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Values.Any(double.IsNaN), Is.True);
            Assert.That(progress.Values[^1], Is.EqualTo(1d));
            Assert.That(File.ReadAllBytes(temp.Path), Is.EqualTo(bytes));
        });
    }

    private static FileDownloadService CreateService(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new FileDownloadService(
            new StaticHttpClientFactory(new HttpClient(new DelegateHandler(handler))),
            NullLogger<FileDownloadService>.Instance);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(bytes).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.CreateVersion7() + ".bin");

        public void Dispose()
        {
            File.Delete(Path);
            File.Delete(Path + ".part");
        }
    }
}
