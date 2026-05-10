using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.BuiltIn;
using Everywhere.Chat.Plugins.Mcp;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Database;
using Everywhere.Storage;
using Everywhere.Views;
using Everywhere.Views.Pages;
using Everywhere.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Extensions;

public static class ServiceExtension
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddAvaloniaBasicServices()
        {
            return services
                .AddDialogManagerAndToastManager()
                .AddDialogAndToastExceptionHandler();
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