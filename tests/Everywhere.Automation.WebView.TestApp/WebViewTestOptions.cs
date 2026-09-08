using System.Diagnostics.CodeAnalysis;
using Everywhere.Automation.TestApp;

namespace Everywhere.Automation.WebView.TestApp;

internal sealed record WebViewTestOptions(TestAppOptions Common, Uri Address)
{
    public static WebViewTestOptions Parse(params IReadOnlyList<string> arguments)
    {
        var commonArguments = new List<string>(4);
        Uri? address = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] != "--url")
            {
                commonArguments.Add(arguments[index]);
                continue;
            }

            if (++index >= arguments.Count || !TryParseAddress(arguments[index], out address)) throw new ArgumentException("The WebView --url argument must contain an absolute HTTP or HTTPS address.", nameof(arguments));
        }

        if (address is null) throw new ArgumentException("The WebView TestApp requires --url <http-or-https-address>.", nameof(arguments));
        return new WebViewTestOptions(TestAppOptions.Parse(commonArguments), address);
    }

    public static bool TryParseAddress(string? value, [NotNullWhen(true)] out Uri? address)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) && (candidate.Scheme == Uri.UriSchemeHttp || candidate.Scheme == Uri.UriSchemeHttps))
        {
            address = candidate;
            return true;
        }

        address = null;
        return false;
    }
}
