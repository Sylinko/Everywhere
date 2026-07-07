using Everywhere.Views;
using Everywhere.Views.Pages;
using Pure.DI;
using ShadUI;
using static Pure.DI.Lifetime;

namespace Everywhere.DependencyInjection;

public partial class CoreComposition
{
    // ReSharper disable once UnusedMember.Local
    private static void SetupAvaloniaServices() =>
        DI.Setup()
            // UI host services that require Avalonia runtime state.
            .Bind<DialogManager>().To(_ => ApplicationServiceProviderFactories.CreateDialogManager())
            .Bind<ToastHost>().To(_ => ApplicationServiceProviderFactories.CreateToastHost())
            .Bind<VisualTreeDebugger>().As(Singleton).To<VisualTreeDebugger>()

            // Chat window shell and animation target.
            .Bind<ChatWindowViewModel>().As(Singleton).To<ChatWindowViewModel>()
            .Bind<ChatWindow>().Bind<IVisualElementAnimationTarget>().As(Singleton).To<ChatWindow>()

            // Main navigation pages.
            .Bind<HomePageViewModel>().As(Singleton).To<HomePageViewModel>()
            .Bind<HomePage>().Bind<IMainViewNavigationItem>(Tag.Unique).As(Singleton).To<HomePage>()
            .Bind<CustomAssistantPageViewModel>().As(Singleton).To<CustomAssistantPageViewModel>()
            .Bind<CustomAssistantPage>().Bind<IMainViewNavigationItem>(Tag.Unique).As(Singleton).To<CustomAssistantPage>()
            .Bind<PromptPageViewModel>().As(Singleton).To<PromptPageViewModel>()
            .Bind<PromptPage>().Bind<IMainViewNavigationItem>(Tag.Unique).As(Singleton).To<PromptPage>()
            .Bind<PromptEditorViewModel>().To<PromptEditorViewModel>()
            .Bind<PromptEditorPage>().To<PromptEditorPage>()
            .Bind<ChatPluginPageViewModel>().As(Singleton).To<ChatPluginPageViewModel>()
            .Bind<ChatPluginPage>().Bind<IMainViewNavigationItem>(Tag.Unique).As(Singleton).To<ChatPluginPage>()
            .Bind<SkillPageViewModel>().As(Singleton).To<SkillPageViewModel>()
            .Bind<SkillPage>().Bind<IMainViewNavigationItem>(Tag.Unique).As(Singleton).To<SkillPage>()
            .Bind<WebSearchEnginePageViewModel>().As(Singleton).To<WebSearchEnginePageViewModel>()
            .Bind<WebSearchEnginePage>().Bind<IMainViewNavigationItem>(Tag.Unique).As(Singleton).To<WebSearchEnginePage>()
            .Bind<SettingsPage>().Bind<IMainViewNavigationItem>(Tag.Unique).To<SettingsPage>()

            // Secondary views and app shell.
            .Bind<WelcomeViewModel>().To<WelcomeViewModel>()
            .Bind<WelcomeView>().To<WelcomeView>()
            .Bind<ChangeLogViewModel>().To<ChangeLogViewModel>()
            .Bind<ChangeLogView>().To<ChangeLogView>()
            .Bind<MainViewModel>().As(Singleton).To<MainViewModel>()
            .Bind<MainView>().As(Singleton).To<MainView>()

            // Visual effects.
            .Bind<VisualElementEffect>().As(Singleton).To<VisualElementEffect>();
}