using System.Diagnostics.CodeAnalysis;
using System.Net;
using Everywhere.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Cloud.DependencyInjection;

public static class CloudServiceCollection
{
    public static IServiceCollection Configure(IServiceCollection services)
    {
        // Cloud HTTP user-agent handler.
        services.AddTransient<CloudUserAgentHandler>();

        // Cloud API named HTTP client.
        services
            .AddHttpClient(
                nameof(ICloudClient),
                client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHttpClientHandler(sp.GetRequiredService<IWebProxy>()))
            .AddHttpMessageHandler(sp => sp.GetRequiredService<ICloudClient>().CreateAuthenticationHandler())
            .AddHttpMessageHandler<CloudUserAgentHandler>();

        return services;
    }

    public static IServiceCollection ConfigureAliases(IServiceCollection services)
    {
        // Cloud startup initializer aliases.
        services.AddSingleton<IAsyncInitializer>(sp => sp.GetRequiredService<OAuthCloudClient>());
        services.AddSingleton<IAsyncInitializer>(sp => sp.GetRequiredService<CloudChatDbSynchronizer>());
        return services;
    }

    private static HttpClientHandler CreateHttpClientHandler(IWebProxy proxy) =>
        new()
        {
            Proxy = proxy,
            UseProxy = true,
            AllowAutoRedirect = true,
        };

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    private sealed class CloudUserAgentHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove("User-Agent");
            request.Headers.Add("User-Agent", $"Everywhere/{App.Version}");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
