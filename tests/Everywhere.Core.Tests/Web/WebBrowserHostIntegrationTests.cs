using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.ProcessIsolation.Watchdog;
using Everywhere.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Everywhere.Core.Tests.Web;

[Explicit("Starts a real browser against a local HTTP server.")]
[Category("BrowserIntegration")]
public sealed class WebBrowserHostIntegrationTests
{
    [TestCase("/markdown", nameof(WebExtractionSource.MarkdownResponse), "Markdown body")]
    [TestCase("/text", nameof(WebExtractionSource.PlainTextResponse), "Plain text body")]
    [TestCase("/html", null, "Readability hostile content")]
    public async Task ExtractPageAsync_ReadsLocalServerPages(string path, string? expectedSource, string expectedText)
    {
        await using var server = await LocalHttpServer.StartAsync(
            new Dictionary<string, (string ContentType, string Body)>
            {
                ["/markdown"] = ("text/markdown; charset=utf-8", "# Title\n\nMarkdown body"),
                ["/text"] = ("text/plain; charset=utf-8", "Plain text body"),
                ["/html"] = (
                    "text/html; charset=utf-8",
                    """
                    <!doctype html>
                    <html>
                      <body>
                        <nav>Navigation should not dominate</nav>
                        <div class="content">
                          <h1>Article</h1>
                          <p>Readability hostile content lives here.</p>
                        </div>
                      </body>
                    </html>
                    """)
            });
        var host = CreateHost();

        var result = await host.ExtractPageAsync(server.Url(path));

        Assert.Multiple(() =>
        {
            if (expectedSource is not null)
            {
                Assert.That(result.Source, Is.EqualTo(Enum.Parse<WebExtractionSource>(expectedSource)));
            }

            Assert.That(result.Markdown, Does.Contain(expectedText));
        });
    }

    [Test]
    public async Task ExtractPageAsync_OverridesMainDocumentAcceptHeader()
    {
        await using var server = await LocalHttpServer.StartAsync(
            request =>
            {
                var acceptsMarkdown = request.Headers.TryGetValue("Accept", out var accept) &&
                    accept.Contains("text/markdown", StringComparison.OrdinalIgnoreCase);

                return acceptsMarkdown ?
                    new LocalHttpResponse("text/markdown; charset=utf-8", "# Negotiated\n\nMarkdown body from Accept header") :
                    new LocalHttpResponse("text/html; charset=utf-8", "<!doctype html><body>HTML fallback body</body>");
            });
        var host = CreateHost();

        var result = await host.ExtractPageAsync(server.Url("/negotiated"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Source, Is.EqualTo(WebExtractionSource.MarkdownResponse));
            Assert.That(result.Markdown, Does.Contain("Markdown body from Accept header"));
            Assert.That(result.Markdown, Does.Not.Contain("HTML fallback body"));
        });
    }

    [Test]
    public async Task OpenBrowserThenExtractPageAsync_ReusesSharedBrowserPath()
    {
        await using var server = await LocalHttpServer.StartAsync(
            new Dictionary<string, (string ContentType, string Body)>
            {
                ["/markdown"] = ("text/markdown; charset=utf-8", "# Title\n\nMarkdown body after manual browser")
            });
        var host = CreateHost();

        await host.OpenBrowserAsync();
        var result = await host.ExtractPageAsync(server.Url("/markdown"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Source, Is.EqualTo(WebExtractionSource.MarkdownResponse));
            Assert.That(result.Markdown, Does.Contain("Markdown body after manual browser"));
        });
    }

    private static WebBrowserHost CreateHost()
    {
        EnsureLocaleManager();

        var settings = new Settings(new ServiceCollection().BuildServiceProvider());
        settings.Plugin.WebBrowser.ShowBrowser = false;

        var watchdogManager = Substitute.For<IWatchdogManager>();
        watchdogManager.RegisterProcessAsync(Arg.Any<int>()).Returns(Task.FromResult<WatchdogRegistration?>(null));
        watchdogManager.RegisterProcessAsync(Arg.Any<Process>()).Returns(Task.FromResult<WatchdogRegistration?>(null));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        return new WebBrowserHost(
            settings,
            watchdogManager,
            httpClientFactory,
            LoggerFactory.Create(_ => { }));
    }

    private static void EnsureLocaleManager()
    {
        try
        {
            _ = LocaleManager.Shared;
        }
        catch (InvalidOperationException)
        {
            _ = new LocaleManager();
        }
    }

    private sealed class LocalHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<LocalHttpRequest, LocalHttpResponse> _handler;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serverTask;

        private LocalHttpServer(TcpListener listener, Func<LocalHttpRequest, LocalHttpResponse> handler)
        {
            _listener = listener;
            _handler = handler;
            _serverTask = Task.Run(ServeAsync);
        }

        public static Task<LocalHttpServer> StartAsync(IReadOnlyDictionary<string, (string ContentType, string Body)> routes)
        {
            return StartAsync(request =>
            {
                var route = routes.TryGetValue(request.Path, out var found)
                    ? new LocalHttpResponse(found.ContentType, found.Body, "200 OK")
                    : new LocalHttpResponse("text/plain; charset=utf-8", "Not found", "404 Not Found");
                return route;
            });
        }

        public static Task<LocalHttpServer> StartAsync(Func<LocalHttpRequest, LocalHttpResponse> handler)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new LocalHttpServer(listener, handler));
        }

        public string Url(string path)
        {
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            return $"http://127.0.0.1:{port}{path}";
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try { await _serverTask; }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }

            _cts.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleClientAsync(client), _cts.Token);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using var _ = client;
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(_cts.Token);
            var path = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "/";

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_cts.Token)))
            {
                var separatorIndex = line.IndexOf(':');
                if (separatorIndex <= 0) continue;

                headers[line[..separatorIndex]] = line[(separatorIndex + 1)..].Trim();
            }

            var response = _handler(new LocalHttpRequest(path, headers));
            var body = Encoding.UTF8.GetBytes(response.Body);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {response.Status}\r\nContent-Type: {response.ContentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");

            await client.GetStream().WriteAsync(header, _cts.Token);
            await client.GetStream().WriteAsync(body, _cts.Token);
        }
    }

    private sealed record LocalHttpRequest(string Path, IReadOnlyDictionary<string, string> Headers);

    private sealed record LocalHttpResponse(string ContentType, string Body, string Status = "200 OK");
}
