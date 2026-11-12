using System.Diagnostics.CodeAnalysis;
using System.Net;
using Everywhere.Common;
using Everywhere.ViewModels;
using Everywhere.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Cloud;

public static class ServiceExtension
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddCloudClient()
        {
            services.AddSingleton<CloudNotificationSource>();
            services.AddSingleton<IAsyncInitializer>(provider => provider.GetRequiredService<CloudNotificationSource>());
            // Guests use a public transport; authenticated requests reuse the normal token pipeline below.
            services.AddHttpClient(CloudNotificationSource.AnonymousHttpClientName, client =>
                {
                    client.BaseAddress = new Uri(CloudConstants.CloudBaseUrl.TrimEnd('/') + "/");
                    client.Timeout = TimeSpan.FromSeconds(15);
                })
                .ConfigurePrimaryHttpMessageHandler(provider => new HttpClientHandler
                {
                    Proxy = provider.GetRequiredService<IWebProxy>(),
                    UseProxy = true
                })
                .AddHttpMessageHandler(_ => new UserAgentHandler());

            services.AddSingleton<OAuthCloudClient>();
            services.AddSingleton<ICloudClient>(x => x.GetRequiredService<OAuthCloudClient>());
            services.AddSingleton<IAsyncInitializer>(x => x.GetRequiredService<OAuthCloudClient>());
            services.AddTransient<ChatSyncPageViewModel>();
            services.AddSingleton<ChatSyncPage>();
            services.AddSingleton<ChatSyncService>();
            services.AddSingleton<IChatSyncService>(x => x.GetRequiredService<ChatSyncService>());
            services.AddSingleton<IAsyncInitializer>(x => x.GetRequiredService<ChatSyncService>());

            services.AddSingleton<OfficialModelProvider>();
            services.AddSingleton<IOfficialModelProvider>(x => x.GetRequiredService<OfficialModelProvider>());
            services.AddSingleton<IAsyncInitializer>(x => x.GetRequiredService<OfficialModelProvider>());

            AddAuthenticatedHttpClient(services, nameof(ICloudClient), CloudConstants.AIGatewayBaseUrl, TimeSpan.FromSeconds(30));
            AddAuthenticatedHttpClient(services, nameof(CloudNotificationSource), CloudConstants.CloudBaseUrl, TimeSpan.FromSeconds(15));
            // A sync request can stream an entire chat rather than one small part.
            AddAuthenticatedHttpClient(services, nameof(IChatSyncService), CloudConstants.CloudBaseUrl, TimeSpan.FromMinutes(5));
            return services;
        }
    }

    private static void AddAuthenticatedHttpClient(IServiceCollection services, string name, string baseUrl, TimeSpan timeout)
    {
        // This client includes the CloudAuthenticationHandler which:
        // - Automatically adds Bearer token to Authorization header
        // - Handles 401 responses by refreshing the token and retrying
        // Note: Authentication flows (login, token refresh) use the default HttpClient
        // which is already configured with proxy in NetworkInitializer.
        services
            .AddHttpClient(
                name,
                client =>
                {
                    if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var address))
                    {
                        client.BaseAddress = address;
                    }

                    client.Timeout = timeout;
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