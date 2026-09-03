using System.Text.Json;

namespace Everywhere.Automation.CefSharp.Probe;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            PrintUsage();
            return 0;
        }

        var options = ProbeOptions.Parse(args);
        Directory.CreateDirectory(options.OutputDirectory);
        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        if (options.IsMcpServer)
        {
            await McpProbeServer.RunAsync(options, cancellationSource.Token);
            return 0;
        }

        return await RunBatchAsync(options, cancellationSource.Token);
    }

    private static async Task<int> RunBatchAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        var results = new List<ProbeStepResult>();
        await using var session = new CefSharpProbeSession(options);
        for (var index = 0; index < options.Addresses.Count; index++)
        {
            var requestedAddress = options.Addresses[index].AbsoluteUri;
            try
            {
                var navigation = await session.NavigateAsync(requestedAddress, cancellationToken: cancellationToken);
                var query = await session.QueryAsync("root", "child", 1, options.Limit, options.TargetTokenBudget, cancellationToken);
                var result = new ProbeStepResult(index + 1, requestedAddress, navigation.FinalAddress, navigation.Elapsed, query.Elapsed, query.PublishedTargetCount, query.Content.Length, query.RetainedTargetCount, query.RetainedTurnCount, query.OutputPath, null);
                results.Add(result);
                Console.WriteLine($"[{index + 1}/{options.Addresses.Count}] {result.FinalAddress} -> {result.PublishedTargetCount} targets, {result.CharacterCount} chars, navigation {result.NavigationElapsed.TotalSeconds:F2}s, observation {result.ObservationElapsed.TotalSeconds:F2}s, {result.OutputPath}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                results.Add(new ProbeStepResult(index + 1, requestedAddress, null, TimeSpan.Zero, TimeSpan.Zero, 0, 0, session.TargetCount, session.RetainedTurnCount, null, exception.Message));
                Console.Error.WriteLine($"[{index + 1}/{options.Addresses.Count}] {requestedAddress} failed: {exception.Message}");
            }
        }

        var summaryPath = Path.Combine(options.OutputDirectory, "summary.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(results, JsonOptions), cancellationToken);
        Console.WriteLine($"Summary: {summaryPath}");
        return results.Any(static result => result.Error is not null) ? 2 : 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project tests/Everywhere.Automation.CefSharp.Probe -- [options] [URL ...]");
        Console.WriteLine("Options:");
        Console.WriteLine("  --mcp                 Run a long-lived Streamable HTTP MCP server.");
        Console.WriteLine("  --listen <http-url>   MCP listen address; default http://127.0.0.1:5187.");
        Console.WriteLine("  --output <directory>  Artifact directory; defaults under the Probe build output.");
        Console.WriteLine("  --limit <1-256>       Default maximum VisualQuery nodes; default 256.");
        Console.WriteLine("  --budget <tokens>     Default approximate prompt budget; default 32768.");
        Console.WriteLine("  --settle-ms <ms>      Accessibility propagation delay after navigation; default 750.");
        Console.WriteLine("  --timeout-ms <ms>     Per-navigation timeout; default 45000.");
    }
}

internal sealed record ProbeStepResult(
    int Step,
    string RequestedAddress,
    string? FinalAddress,
    TimeSpan NavigationElapsed,
    TimeSpan ObservationElapsed,
    int PublishedTargetCount,
    int CharacterCount,
    int RetainedTargetCount,
    int RetainedTurnCount,
    string? OutputPath,
    string? Error);
