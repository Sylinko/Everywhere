using System.Globalization;
using Everywhere.Chat;

namespace Everywhere.Automation.CefSharp.Probe;

internal sealed record ProbeOptions(bool IsMcpServer, IReadOnlyList<Uri> Addresses, string ListenAddress, string OutputDirectory, int Limit, int TargetTokenBudget, TimeSpan SettleDelay, TimeSpan NavigationTimeout)
{
    public static ProbeOptions Parse(params IReadOnlyList<string> arguments)
    {
        var isMcpServer = false;
        var addresses = new List<Uri>();
        var listenAddress = "http://127.0.0.1:5187";
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "artifacts", DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var limit = VisualQueryRequest.MaximumLimit;
        var targetTokenBudget = 32_768;
        var settleMilliseconds = 750;
        var timeoutMilliseconds = 45_000;
        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--mcp":
                    isMcpServer = true;
                    break;
                case "--listen" when index + 1 < arguments.Count:
                    listenAddress = ParseListenAddress(arguments[++index]);
                    break;
                case "--output" when index + 1 < arguments.Count:
                    outputDirectory = Path.GetFullPath(arguments[++index]);
                    break;
                case "--limit" when index + 1 < arguments.Count:
                    limit = int.Parse(arguments[++index], CultureInfo.InvariantCulture);
                    break;
                case "--budget" when index + 1 < arguments.Count:
                    targetTokenBudget = int.Parse(arguments[++index], CultureInfo.InvariantCulture);
                    break;
                case "--settle-ms" when index + 1 < arguments.Count:
                    settleMilliseconds = int.Parse(arguments[++index], CultureInfo.InvariantCulture);
                    break;
                case "--timeout-ms" when index + 1 < arguments.Count:
                    timeoutMilliseconds = int.Parse(arguments[++index], CultureInfo.InvariantCulture);
                    break;
                case var address when Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps):
                    addresses.Add(uri);
                    break;
                default:
                    throw new ArgumentException($"Unknown option or invalid HTTP/HTTPS URL '{arguments[index]}'.", nameof(arguments));
            }
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, VisualQueryRequest.MaximumLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetTokenBudget);
        ArgumentOutOfRangeException.ThrowIfNegative(settleMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);
        if (addresses.Count == 0)
        {
            addresses.Add(new Uri("https://example.com/"));
            addresses.Add(new Uri("https://www.wikipedia.org/"));
            addresses.Add(new Uri("https://github.com/"));
        }

        return new ProbeOptions(isMcpServer, addresses, listenAddress, outputDirectory, limit, targetTokenBudget, TimeSpan.FromMilliseconds(settleMilliseconds), TimeSpan.FromMilliseconds(timeoutMilliseconds));
    }

    private static string ParseListenAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new ArgumentException("The MCP listen address must be an absolute HTTP authority without a path, query, or fragment.", nameof(value));
        return value.TrimEnd('/');
    }
}
