using System.Diagnostics.CodeAnalysis;
using System.Net;
using Everywhere.Common;
using Everywhere.Database;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Cloud;

public static class ServiceExtension
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddCloudClient()
        {
            services.AddSingleton<OAuthCloudClient>();
            services.AddSingleton<ICloudClient>(x => x.GetRequiredService<OAuthCloudClient>());
            services.AddSingleton<IAsyncInitializer>(x => x.GetRequiredService<OAuthCloudClient>());

            services.AddSingleton<CloudChatDbSynchronizer>();
            services.AddSingleton<IChatDbSynchronizer>(x => x.GetRequiredService<CloudChatDbSynchronizer>());
            services.AddSingleton<IAsyncInitializer>(x => x.GetRequiredService<CloudChatDbSynchronizer>());

            services.AddSingleton<OfficialModelProvider>();
            services.AddSingleton<IOfficialModelProvider>(x => x.GetRequiredService<OfficialModelProvider>());
            services.AddSingleton<IAsyncInitializer>(x => x.GetRequiredService<OfficialModelProvider>());

            // Register the authenticated HttpClient for API requests.
            // This client includes the CloudAuthenticationHandler which:
            // - Automatically adds Bearer token to Authorization header
            // - Handles 401 responses by refreshing the token and retrying
            // Note: Authentication flows (login, token refresh) use the default HttpClient
            // which is already configured with proxy in NetworkInitializer.
            services
                .AddHttpClient(
                    nameof(ICloudClient),
                    client =>
                    {
                        client.Timeout = TimeSpan.FromSeconds(30);
                    })
                .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                    new HttpClientHandler
                    {
                        Proxy = serviceProvider.GetRequiredService<IWebProxy>(),
                        UseProxy = true,
                        AllowAutoRedirect = true,
                    })
                .AddHttpMessageHandler(x => x.GetRequiredService<ICloudClient>().CreateAuthenticationHandler())
                .AddHttpMessageHandler(_ => new UserAgentHandler());

            return services;
        }
    }

    /// <summary>
    /// Force override the User-Agent before send
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    private sealed class UserAgentHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove("User-Agent");
            request.Headers.Add(
                "User-Agent",
                $"Everywhere/{App.Version}");

            return base.SendAsync(request, cancellationToken);
        }
    }
}