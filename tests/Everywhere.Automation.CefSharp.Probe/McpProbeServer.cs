using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace Everywhere.Automation.CefSharp.Probe;

internal static class McpProbeServer
{
    public static async Task RunAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls(options.ListenAddress);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<CefSharpProbeSession>();
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.RunAsync(cancellationToken);
    }
}
