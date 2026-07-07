using System.Diagnostics.CodeAnalysis;
using System.Net;
using Everywhere.AI.Prompts.Database;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.BuiltIn;
using Everywhere.Chat.Plugins.Mcp;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Configuration.Engine;
using Everywhere.Database;
using Everywhere.Initialization;
using Everywhere.Interop;
using Everywhere.Skills;
using Everywhere.Statistics;
using Everywhere.Statistics.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Extensions.Logging;

namespace Everywhere.DependencyInjection;

public static class ApplicationServiceCollection
{
    public static IServiceCollection Configure(IServiceCollection services) =>
        services
            .ConfigureLogging()
            .ConfigureHttpClients()
            .ConfigureEntityFramework();

    // Pure.DI.MS exports roots by their own contract. These aliases preserve
    // MS DI enumerable behavior for services also consumed through aggregate
    // contracts such as IAsyncInitializer and BuiltInChatPlugin.
    public static IServiceCollection ConfigureCoreAliases(IServiceCollection services)
    {
        // Startup initializer aliases.
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<SettingsEngine>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<PersistentKeyValueStorage>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<CustomAssistantInitializer>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<ChatDbInitializer>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<PromptDbInitializer>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<StatisticsDbInitializer>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<StatisticsBackfiller>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<SkillManager>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<ChatContextManager>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<WatchdogManager>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<RuntimeManager>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<NetworkInitializer>());

        // Built-in chat plugin aliases.
        services.AddTransient<BuiltInChatPlugin>(x => x.GetRequiredService<EssentialPlugin>());
        services.AddTransient<BuiltInChatPlugin>(x => x.GetRequiredService<VisualContextPlugin>());
        services.AddTransient<BuiltInChatPlugin>(x => x.GetRequiredService<FileSystemPlugin>());
        services.AddTransient<BuiltInChatPlugin>(x => x.GetRequiredService<WebPlugin>());
        services.AddTransient<BuiltInChatPlugin>(x => x.GetRequiredService<TerminalPlugin>());

        return services;
    }

    // Platform initializers and plugins are registered after the platform
    // composition exports its concrete roots.
    public static IServiceCollection ConfigurePlatformAliases<TPlatformPlugin>(IServiceCollection services) where TPlatformPlugin : BuiltInChatPlugin
    {
        // Platform startup initializer aliases.
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<ChatWindowInitializer>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<UpdaterInitializer>());

        // Platform chat plugin alias.
        services.AddTransient<BuiltInChatPlugin>(x => x.GetRequiredService<TPlatformPlugin>());
        return services;
    }

    private static IServiceCollection ConfigureLogging(this IServiceCollection services) =>
        services.AddLogging(builder => builder
#if DEBUG
            .SetMinimumLevel(LogLevel.Debug)
#endif
            .AddSerilog(dispose: true)
            .AddFilter<SerilogLoggerProvider>("Microsoft.EntityFrameworkCore", LogLevel.Warning));

    private static IServiceCollection ConfigureHttpClients(this IServiceCollection services)
    {
        // Delegating handlers used by named clients.
        services.AddTransient<DefaultUserAgentHandler>();
        services.AddTransient<CloudUserAgentHandler>();
        services.AddTransient<ContentLengthBufferingHandler>();
        services.AddTransient<McpSessionExpiryHandler>();

        // Default app HTTP client.
        services
            .AddHttpClient(
                Options.DefaultName,
                client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(x => CreateHttpClientHandler(x.GetRequiredService<IWebProxy>()))
            .AddHttpMessageHandler<DefaultUserAgentHandler>();

        // MCP transport HTTP client.
        services
            .AddHttpClient(
                McpServiceExtension.McpClientName,
                client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(x => CreateHttpClientHandler(x.GetRequiredService<IWebProxy>()))
            .AddHttpMessageHandler<ContentLengthBufferingHandler>()
            .AddHttpMessageHandler<McpSessionExpiryHandler>();

        return services;
    }

    private static IServiceCollection ConfigureEntityFramework(this IServiceCollection services) =>
        services
            // Chat database context factory.
            .AddDbContextFactory<ChatDbContext>((_, options) =>
            {
                var dbPath = RuntimeConstants.GetDatabasePath("chat.db");
                options.UseSqlite($"Data Source={dbPath}");
            })

            // Prompt database context factory.
            .AddDbContextFactory<PromptDbContext>((_, options) =>
            {
                var dbPath = RuntimeConstants.GetDatabasePath("prompt.db");
                options.UseSqlite($"Data Source={dbPath}");
            })

            // Statistics database context factory.
            .AddDbContextFactory<StatisticsDbContext>((_, options) =>
            {
                var dbPath = RuntimeConstants.GetDatabasePath("statistics.db");
                options.UseSqlite($"Data Source={dbPath}");
            });

    private static HttpClientHandler CreateHttpClientHandler(IWebProxy proxy) =>
        new()
        {
            Proxy = proxy,
            UseProxy = true,
            AllowAutoRedirect = true,
        };

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    private sealed class DefaultUserAgentHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove("User-Agent");
            request.Headers.Add("User-Agent", $"Chrome/142.0.0.0 Safari/537.36 Everywhere/{App.Version}");
            return base.SendAsync(request, cancellationToken);
        }
    }

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