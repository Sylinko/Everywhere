using System.Diagnostics.CodeAnalysis;
using System.Net;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShadUI;

namespace Everywhere.Initialization;

/// <summary>
/// Applies the configured network proxy to global HTTP handlers and keeps it in sync with user settings.
/// </summary>
public sealed class NetworkInitializer : IAsyncInitializer, IDisposable
{
    private readonly ProxySettings _proxySettings;
    private readonly DynamicWebProxy _dynamicWebProxy;
    private readonly ILogger<NetworkInitializer> _logger;
    private readonly DebounceExecutor<NetworkInitializer, ThreadingTimerImpl> _applyProxyDebounceExecutor;
    private DeepObserver? _settingsObserver;

    public NetworkInitializer(Settings settings, DynamicWebProxy dynamicWebProxy, ILogger<NetworkInitializer> logger)
    {
        _proxySettings = settings.Proxy;
        _dynamicWebProxy = dynamicWebProxy;
        _logger = logger;
        _applyProxyDebounceExecutor = new DebounceExecutor<NetworkInitializer, ThreadingTimerImpl>(
            () => this,
            static that => that.ApplyProxySettings(true),
            TimeSpan.FromSeconds(0.5));
    }

    #region IAsyncInitializer

    /// <inheritdoc />
    static AsyncInitializerIndex IAsyncInitializer.Index => AsyncInitializerIndex.Network;

    /// <inheritdoc />
    Task IAsyncInitializer.InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyProxySettings(false);
        _settingsObserver = new DeepObserver(HandleProxySettingsChanged).Observe(_proxySettings);

        return Task.CompletedTask;
    }

    #endregion

    /// <summary>
    /// Stops proxy setting observation and cancels pending debounced updates.
    /// </summary>
    public void Dispose()
    {
        _settingsObserver?.Dispose();
        _applyProxyDebounceExecutor.Dispose();
    }

    private void HandleProxySettingsChanged(in DeepObserverChangedEventArgs e)
    {
        _applyProxyDebounceExecutor.Trigger();
    }

    private void ApplyProxySettings(bool notifyOnError)
    {
        try
        {
            _dynamicWebProxy.ApplyProxySettings(_proxySettings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to apply proxy settings: {Message}", ex);

            if (notifyOnError)
            {
                ToastManager.Error(LocaleKey.Common_Error.I18N(), ex.GetFriendlyMessage().ToTextBlock());
            }
        }
    }
}

/// <summary>
/// Extension methods for configuring network services.
/// </summary>
public static class NetworkExtension
{
    /// <summary>
    /// Configures network services with proxy settings from the application settings.
    /// This method registers a singleton <see cref="DynamicWebProxy"/> to handle proxying HTTP requests.
    /// It also configures the default <see cref="HttpClient"/> to use this dynamic proxy.
    /// Then adds the <see cref="NetworkInitializer"/> to handle dynamic changes and initial application of proxy settings.
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection ConfigureNetwork(this IServiceCollection services)
    {
        // Register DynamicWebProxy as a singleton to manage proxy settings.
        services
            .AddSingleton<DynamicWebProxy>()
            .AddSingleton<IWebProxy>(x => x.GetRequiredService<DynamicWebProxy>())
            .AddSingleton<FileDownloadService>()
            .AddSingleton<IFileDownloadService>(x => x.GetRequiredService<FileDownloadService>())
            .AddSingleton<RuntimeManager>()
            .AddSingleton<IRuntimeManager>(x => x.GetRequiredService<RuntimeManager>())
            .AddTransient<UserAgentHandler>()
            .AddAsyncInitializer<NetworkInitializer>()
            .AddAsyncInitializer<RuntimeManager>();

        // Configure the default HttpClient to use the DynamicWebProxy.
        services
            .AddHttpClient(
                Options.DefaultName,
                client =>
                {
                    // Set a short timeout for HTTP requests.
                    client.Timeout = TimeSpan.FromSeconds(10);
                })
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                new HttpClientHandler
                {
                    // Resolve the singleton instance of DynamicWebProxy.
                    Proxy = serviceProvider.GetRequiredService<IWebProxy>(),
                    UseProxy = true,
                    AllowAutoRedirect = true,
                })
            .AddHttpMessageHandler<UserAgentHandler>();

        return services;
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
                $"Chrome/142.0.0.0 Safari/537.36 Everywhere/{App.Version}");

            return base.SendAsync(request, cancellationToken);
        }
    }
}
