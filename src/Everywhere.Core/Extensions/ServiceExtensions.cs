using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.BuiltIn;
using Everywhere.Chat.Plugins.Mcp;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Database;
using Everywhere.Skills;
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

        public IServiceCollection AddAvaloniaBasicServices()
        {
            return services.AddDialogManagerAndToastManager();
        }

        public IServiceCollection AddViewsAndViewModels() =>
            services
                .AddSingleton<VisualTreeDebugger>()
                .AddSingleton<ChatWindowViewModel>()
                .AddSingleton<ChatWindow>()
                .AddSingleton<CustomAssistantPageViewModel>()
                .AddSingleton<IMainViewNavigationItem, CustomAssistantPage>()
                .AddSingleton<ChatPluginPageViewModel>()
                .AddSingleton<IMainViewNavigationItem, ChatPluginPage>()
                .AddSingleton<SkillPageViewModel>()
                .AddSingleton<IMainViewNavigationItem, SkillPage>()
                .AddSingleton<WebSearchEnginePageViewModel>()
                .AddSingleton<IMainViewNavigationItem, WebSearchEnginePage>()
                .AddTransient<IMainViewNavigationItem, SettingsPage>()
                .AddSingleton<AboutPageViewModel>()
                .AddSingleton<IMainViewNavigationItem, AboutPage>()
                .AddTransient<WelcomeViewModel>()
                .AddTransient<WelcomeView>()
                .AddTransient<ChangeLogViewModel>()
                .AddTransient<ChangeLogView>()
                .AddSingleton<MainViewModel>()
                .AddSingleton<MainView>()
                .AddSingleton<IVisualElementAnimationTarget>(x => x.GetRequiredService<ChatWindow>())
                .AddSingleton<VisualElementEffect>();

        public IServiceCollection AddDatabaseAndStorage() =>
            services
                .AddDbContextFactory<ChatDbContext>((_, options) =>
                {
                    var dbPath = RuntimeConstants.GetDatabasePath("chat.db");
                    options.UseSqlite($"Data Source={dbPath}");
                })
                .AddSingleton<IBlobStorage, BlobStorage>()
                .AddSingleton<IChatContextStorage, ChatContextStorage>()
                .AddTransient<IAsyncInitializer, ChatDbInitializer>();

        public IServiceCollection AddChatEssentials() =>
            services
                .AddSingleton<IKernelMixinFactory, KernelMixinFactory>()
                .AddSingleton<IChatPluginManager, ChatPluginManager>()
                .AddSingleton<SkillSource>()
                .AddSingleton<SkillManager>()
                .AddSingleton<ISkillManager>(x => x.GetRequiredService<SkillManager>())
                .AddSingleton<ISkillPromptProvider>(x => x.GetRequiredService<SkillManager>())
                .AddTransient<IAsyncInitializer>(x => x.GetRequiredService<SkillManager>())
                .AddSingleton<IChatWindowNotificationService, ChatWindowNotificationService>()
                .AddSingleton<IChatService, ChatService>()
                .AddSingleton<IGreetings, Greetings>()
                .AddSingleton<IWebBrowserHost, WebBrowserHost>()
                .AddChatContextManager()
                .AddManagedMcp()

                // Add built-in plugins
                .AddTransient<BuiltInChatPlugin, EssentialPlugin>()
                .AddTransient<BuiltInChatPlugin, VisualContextPlugin>()
                .AddTransient<BuiltInChatPlugin, FileSystemPlugin>()
                .AddTransient<BuiltInChatPlugin, WebPlugin>()
                .AddTransient<BuiltInChatPlugin, TerminalPlugin>();

    }
}
