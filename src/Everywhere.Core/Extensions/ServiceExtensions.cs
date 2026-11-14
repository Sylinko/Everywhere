using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.AI.Prompts.Database;
using Everywhere.Automation;
using Everywhere.Chat;
using Everywhere.Chat.Links;
using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.BuiltIn;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem;
using Everywhere.Chat.Plugins.Mcp;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Configuration.Engine;
using Everywhere.Database;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Input;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.Skills;
using Everywhere.Statistics;
using Everywhere.Statistics.Database;
using Everywhere.Storage;
using Everywhere.Views;
using Everywhere.Views.Pages;
using Everywhere.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace Everywhere.Extensions;

public static class ServiceExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddApplicationLogging() =>
            services.AddLogging(builder => builder
#if DEBUG
                .SetMinimumLevel(LogLevel.Debug)
#endif
                .AddSerilog(dispose: true)
                .AddFilter<SerilogLoggerProvider>("Microsoft.EntityFrameworkCore", LogLevel.Warning));

        /// <summary>Registers Main's process-isolation lifecycle services.</summary>
        public IServiceCollection AddProcessIsolation() =>
            services
                .AddSingleton<HostProcessCoordinator>(sp => new HostProcessCoordinator(
                    sp.GetRequiredService<INamedPipePeerVerifier>(),
                    sp.GetService<IHostsServiceModeManager>()))
                .AddSingleton<IHostConnectionSource>(sp => sp.GetRequiredService<HostProcessCoordinator>())
                .AddSingleton(sp => new ChatVisualService(sp.GetRequiredService<IHostConnectionSource>()))
                .AddSingleton(sp => new DebuggerVisualContext(sp.GetRequiredService<IHostConnectionSource>()))
                .AddSingleton<MainHostControlServer>(sp => MainHostControlServer.Create(
                    sp.GetRequiredService<HostProcessCoordinator>(),
                    sp.GetRequiredService<INamedPipePeerVerifier>()))
                .AddSingleton<IAsyncInitializer>(sp => sp.GetRequiredService<HostProcessCoordinator>())
                .AddSingleton<IAsyncInitializer>(sp => sp.GetRequiredService<MainHostControlServer>());

        /// <summary>Registers Main's connection-restoring Input Host proxy.</summary>
        public IServiceCollection AddInputHostShortcutListener() =>
            services.AddSingleton<IShortcutListener>(sp =>
                new InputHostShortcutListener(
                    sp.GetRequiredService<IHostConnectionSource>(),
                    sp.GetRequiredService<ILogger<InputHostShortcutListener>>()));

        public IServiceCollection AddSettings() =>
            services
                .AddSingleton<Settings>()
                .AddTransient<IAsyncInitializer, SettingsEngine>()
                .AddTransient<SoftwareUpdateControl>()
                .AddTransient<HostsStatusControl>()
                .AddTransient<TextSelectionApplicationsControl>()
                .AddTransient<TextSelectionToolbarActionsControl>()
                .AddTransient<TextSelectionToolbarPreview>()
#if WINDOWS
                .AddTransient<HostsServiceModeControl>()
#endif
                .AddTransient<OpenWebBrowserControl>()
                .AddTransient<DebugFeaturesControl>()
                .AddSingleton<FontFamilyCatalog>()
                .AddSingleton<PersistentKeyValueStorage>()
                .AddSingleton<IKeyValueStorage>(sp => sp.GetRequiredService<PersistentKeyValueStorage>())
                .AddTransient<IAsyncInitializer>(sp => sp.GetRequiredService<PersistentKeyValueStorage>())
                .AddSingleton<PersistentState>();

        public IServiceCollection AddViewsAndViewModels() =>
            services
                .AddSingleton<VisualTreeDebugger>()
                .AddSingleton(provider => new ChatLinkInteraction(
                    provider.GetRequiredService<ILinkFileOperations>(),
                    provider.GetRequiredService<WebPreviewService>(),
                    provider.GetRequiredService<Settings>().ChatWindow,
                    provider.GetRequiredService<ILogger<ChatLinkInteraction>>()))
                .AddSingleton<ChatWindowViewModel>()
                .AddSingleton<ChatWindow>()
                .AddSingleton<HomePageViewModel>()
                .AddSingleton<IMainViewNavigationItem, HomePage>()
                .AddSingleton<CustomAssistantPageViewModel>()
                .AddSingleton<IMainViewNavigationItem, CustomAssistantPage>()
                .AddSingleton<PromptPageViewModel>()
                .AddSingleton<IMainViewNavigationItem, PromptPage>()
                .AddTransient<PromptEditorViewModel>()
                .AddTransient<PromptEditorPage>()
                .AddSingleton<ChatPluginPageViewModel>()
                .AddSingleton<IMainViewNavigationItem, ChatPluginPage>()
                .AddSingleton<SkillPageViewModel>()
                .AddSingleton<IMainViewNavigationItem, SkillPage>()
                .AddTransient<IMainViewNavigationItem, SettingsPage>()
                .AddTransient<WelcomeViewModel>()
                .AddTransient<WelcomeView>()
                .AddTransient<ChangeLogViewModel>()
                .AddTransient<ChangeLogView>()
                .AddSingleton<MainViewModel>()
                .AddSingleton<MainView>()
                .AddSingleton<VisualElementEffect>()
                .AddSingleton<IVisualContextScanEffect>(x => x.GetRequiredService<VisualElementEffect>());

        /// <summary>
        /// Registers database factories and storage, allowing final chat and statistics writes
        /// through already-injected dependencies during ordinary DI disposal.
        /// </summary>
        /// <remarks>
        /// The platform entry points await this shutdown sequence after Avalonia exits:
        /// <code>
        /// ServiceProvider.DisposeAsync() marks the root provider as disposed
        ///   -> ChatContextManager.DisposeAsync()
        ///      -> cancel chat work and await its final model/statistics updates
        ///      -> commit the remaining incremental chat writes
        /// </code>
        /// AddDbContextFactory normally stores the application IServiceProvider in its options.
        /// A newly created context can then resolve ILoggerFactory from that provider on its
        /// first operation, even though the factory and its consumer were already constructed.
        /// Such a resolution fails during DI disposal, before the consumer can save its data.
        /// Merely using the options-only registration overload does not remove this dependency.
        ///
        /// Capture the existing ILoggerFactory during options creation, then clear the implicit
        /// application-provider reference. DI constructs the logger factory before these options
        /// and their consumers, so it remains available until their disposal has completed.
        /// This preserves application logging without resolving services from a closed provider.
        /// EF still owns its internal services, and each storage operation still owns its context;
        /// there is no second application container or special shutdown database connection.
        /// </remarks>
        public IServiceCollection AddDatabaseAndStorage() =>
            services
                .AddDbContextFactory<ChatV2DbContext>((provider, options) =>
                {
                    var dbPath = RuntimeConstants.GetDatabasePath("chat-v2.db");
                    options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    {
                        DataSource = dbPath,
                        ForeignKeys = true,
                    }.ToString());
                    options.UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>());
                    options.UseApplicationServiceProvider(null);
                })
                // Prompt Manager owns an isolated database. The built-in default prompt is virtual
                // and is provided by IDefaultPromptProvider rather than inserted into this database.
                .AddDbContextFactory<PromptDbContext>((_, options) =>
                {
                    var dbPath = RuntimeConstants.GetDatabasePath("prompt.db");
                    options.UseSqlite($"Data Source={dbPath}");
                })
                .AddDbContextFactory<StatisticsDbContext>((provider, options) =>
                {
                    var dbPath = RuntimeConstants.GetDatabasePath("statistics.db");
                    options.UseSqlite($"Data Source={dbPath}");
                    // Canceled requests complete their invocation statistics before the final
                    // chat save. Use the same disposal-safe dependencies described above.
                    options.UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>());
                    options.UseApplicationServiceProvider(null);
                })
                .AddSingleton<IDefaultPromptProvider, DefaultPromptProvider>()
                .AddSingleton<IPromptService, PromptService>()
                .AddSingleton<IAssistantPromptReferenceService, AssistantPromptReferenceService>()
                .AddSingleton<IBlobStorage, BlobStorage>()
                .AddSingleton<ChatTurnStorage>()
                .AddSingleton<IChatContextStorage>(provider => provider.GetRequiredService<ChatTurnStorage>())
                .AddSingleton(provider => new ChatStoreUpgradeService(
                    provider.GetRequiredService<IDbContextFactory<ChatV2DbContext>>(),
                    RuntimeConstants.GetDatabasePath("chat.db"),
                    provider.GetRequiredService<INativeHelper>()))
                .AddSingleton<NotificationCenter>()
                .AddSingleton<INotificationCenter>(x => x.GetRequiredService<NotificationCenter>())
                .AddSingleton(typeof(INotificationPublisher<>), typeof(NotificationPublisher<>))
                .AddSingleton<IStatisticsRecorder, StatisticsRecorder>()
                .AddSingleton<IStatisticsService, StatisticsService>()
                .AddTransient<IAsyncInitializer>(provider => new ChatV2StoreInitializer(
                    provider.GetRequiredService<IDbContextFactory<ChatV2DbContext>>(),
                    RuntimeConstants.GetDatabasePath("chat.db"),
                    provider.GetRequiredService<ILogger<ChatV2StoreInitializer>>()))
                .AddTransient<IAsyncInitializer, ChatStoreUpgradeNoticeInitializer>()
                .AddTransient<IAsyncInitializer, PromptDbInitializer>()
                .AddTransient<IAsyncInitializer, StatisticsDbInitializer>()
                .AddTransient<IAsyncInitializer, StatisticsBackfiller>();

        public IServiceCollection AddChatEssentials() =>
            services
                .AddSingleton<ModelsDevPresetModelProvider>()
                .AddSingleton<IPresetModelProvider>(x => x.GetRequiredService<ModelsDevPresetModelProvider>())
                .AddTransient<IAsyncInitializer>(x => x.GetRequiredService<ModelsDevPresetModelProvider>())
                .AddSingleton<AssistantCatalog>()
                .AddSingleton<AssistantCatalogSynchronizer>()
                .AddTransient<IAsyncInitializer>(x => x.GetRequiredService<AssistantCatalogSynchronizer>())
                .AddSingleton<AssistantSpecializationResolver>()
                .AddSingleton<IKernelMixinFactory, KernelMixinFactory>()
                .AddSingleton<IChatPluginManager, ChatPluginManager>()
                .AddSingleton<SkillSource>()
                .AddSingleton<IVirtualSkillProvider, EmbeddedSkillProvider>()
                .AddSingleton<SkillManager>()
                .AddSingleton<ISkillManager>(x => x.GetRequiredService<SkillManager>())
                .AddSingleton<ISkillPromptProvider>(x => x.GetRequiredService<SkillManager>())
                .AddTransient<IAsyncInitializer>(x => x.GetRequiredService<SkillManager>())
                .AddSingleton<FileHandler, SkillFileHandler>()
                .AddSingleton<FileHandler, PdfFileHandler>()
                .AddSingleton<FileHandler, TextFileHandler>()
                .AddSingleton<FileHandler, BinaryFileHandler>()
                .AddSingleton<FileHandlerContextFactory>()
                .AddSingleton<IChatService, ChatService>()
                .AddSingleton<IGreetings, Greetings>()
                .AddSingleton<IWebBrowserHost, WebBrowserHost>()
                .AddChatContextManager()
                .AddManagedMcp()

                // Add built-in plugins
                .AddSingleton<BuiltInChatPlugin, EssentialPlugin>()
                .AddSingleton<BuiltInChatPlugin, VisualContextPlugin>()
                .AddSingleton<BuiltInChatPlugin, FileSystemPlugin>()
                .AddSingleton<BuiltInChatPlugin, WebPlugin>()
                .AddSingleton<BuiltInChatPlugin, TerminalPlugin>()
                .AddSingleton<BuiltInChatPlugin, OfficeCLIPlugin>();
    }
}