using System.Diagnostics;
using Everywhere.Configuration;
using Everywhere.ProcessIsolation.Watchdog;
using Everywhere.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Core.Tests.Web;

[Explicit("Live web extraction diagnostics. Requires network and a browser.")]
[Category("WebExtractionDiagnostics")]
public sealed class WebExtractionDiagnosticsTests
{
    [TestCase(
        "microsoft-learn-csharp-tour",
        "https://learn.microsoft.com/zh-cn/dotnet/csharp/tour-of-csharp/",
        TestName = "Dump_MicrosoftLearn_CSharpTour")]
    [TestCase(
        "stackoverflow-branch-prediction",
        "https://stackoverflow.com/questions/11227809/why-is-processing-a-sorted-array-faster-than-processing-an-unsorted-array",
        TestName = "Dump_StackOverflow_BranchPrediction")]
    [TestCase(
        "github-microsoft-terminal",
        "https://github.com/microsoft/terminal",
        TestName = "Dump_GitHub_MicrosoftTerminal")]
    [TestCase(
        "apple-macbook-pro",
        "https://www.apple.com/macbook-pro/",
        TestName = "Dump_Apple_MacBookPro")]
    [CancelAfter(180_000)]
    public async Task ExtractPageAsync_DumpKnownComplexPages(string name, string url)
    {
        await RunDiagnosticAsync(name, url);
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task ExtractPageAsync_DumpUrlFromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("EVERYWHERE_WEB_EXTRACT_URL");
        Assert.That(url, Is.Not.Null.And.Not.Empty, "Set EVERYWHERE_WEB_EXTRACT_URL to diagnose an arbitrary URL.");

        await RunDiagnosticAsync("custom-url", url!);
    }

    private static async Task RunDiagnosticAsync(string name, string url)
    {
        var host = CreateHost(showBrowser: false);

        var startedAt = DateTimeOffset.Now;
        TestContext.Progress.WriteLine($"[{startedAt:O}] Extracting {name}");
        TestContext.Progress.WriteLine(url);

        WebPageExtractionResult result;
        try
        {
            result = await host.ExtractPageAsync(url);
        }
        catch (Exception ex)
        {
            var failureSummary = CreateSafeExceptionSummary(ex);
            var failurePath = await WriteMarkdownAsync(name + "-failure", failureSummary);
            TestContext.Out.WriteLine(failureSummary);
            TestContext.Out.WriteLine($"FailurePath: {failurePath}");
            Assert.Fail(failureSummary);
            return;
        }

        var outputPath = await WriteMarkdownAsync(name, result.Markdown);

        TestContext.Out.WriteLine($"Name: {name}");
        TestContext.Out.WriteLine($"Url: {url}");
        TestContext.Out.WriteLine($"Source: {result.Source}");
        TestContext.Out.WriteLine($"Title: {result.Title}");
        TestContext.Out.WriteLine($"ContentLength: {result.ContentLength}");
        TestContext.Out.WriteLine($"Confidence: {result.Selection?.Confidence}");
        if (result.Selection is { } selection)
        {
            foreach (var score in selection.Scores)
            {
                TestContext.Out.WriteLine(
                    $"Candidate: {score.Source} score={score.Score:0.000} confidence={score.Confidence:0.000} length={score.ContentLength} structure={score.Diagnostics.StructureScore:0.000} consensus={score.Diagnostics.ConsensusScore:0.000} linkNoise={score.Diagnostics.LinkNoiseRatio:0.000} duplicates={score.Diagnostics.DuplicateLineRatio:0.000}");
            }
        }

        TestContext.Out.WriteLine($"MarkdownPath: {outputPath}");
        TestContext.Out.WriteLine("Preview:");
        TestContext.Out.WriteLine(CreatePreview(result.Markdown));

        Assert.That(result.Markdown, Is.Not.Empty);
    }

    private static string CreateSafeExceptionSummary(Exception ex)
    {
        var lines = new List<string>
        {
            "Extraction failed.",
            $"ExceptionType: {ex.GetType().FullName}",
            $"Message: {SafeMessage(ex)}"
        };

        var inner = ex.InnerException;
        var depth = 0;
        while (inner is not null && depth < 8)
        {
            lines.Add($"Inner[{depth}].Type: {inner.GetType().FullName}");
            lines.Add($"Inner[{depth}].Message: {SafeMessage(inner)}");
            inner = inner.InnerException;
            depth++;
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string SafeMessage(Exception ex)
    {
        try
        {
            return ex.Message;
        }
        catch (Exception messageException)
        {
            return $"<failed to read message: {messageException.GetType().FullName}>";
        }
    }

    private static WebBrowserHost CreateHost(bool showBrowser)
    {
        var settings = new Settings(new ServiceCollection().BuildServiceProvider());
        settings.Plugin.WebBrowser.ShowBrowser = showBrowser;

        return new WebBrowserHost(
            settings,
            new NoopWatchdogManager(),
            new DefaultHttpClientFactory(),
            LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug)));
    }

    private static async Task<string> WriteMarkdownAsync(string name, string markdown)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Everywhere.WebExtractionDiagnostics");
        Directory.CreateDirectory(directory);

        var fileName = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{SanitizeFileName(name)}.md";
        var path = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(path, markdown);
        return path;
    }

    private static string CreatePreview(string markdown)
    {
        const int maxPreviewLength = 4000;
        markdown = markdown.ReplaceLineEndings("\n").Trim();
        return markdown.Length <= maxPreviewLength
            ? markdown
            : markdown[..maxPreviewLength] + "\n\n... [truncated]";
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalidChar, '-');
        }

        return name;
    }

    private sealed class NoopWatchdogManager : IWatchdogManager
    {
        public Task<WatchdogRegistration?> RegisterProcessAsync(int processId) =>
            Task.FromResult<WatchdogRegistration?>(null);

        public Task<WatchdogRegistration?> RegisterProcessAsync(Process process) =>
            Task.FromResult<WatchdogRegistration?>(null);
    }

    private sealed class DefaultHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
