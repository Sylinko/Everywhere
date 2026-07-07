using System.Net;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.AI.Prompts.Database;
using Everywhere.Chat;
using Everywhere.Chat.Plugins.BuiltIn;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Configuration.Engine;
using Everywhere.Database;
using Everywhere.Initialization;
using Everywhere.Interop;
using Everywhere.Skills;
using Everywhere.Statistics;
using Everywhere.Storage;
using Everywhere.StrategyEngine;
using Everywhere.Views;
using Everywhere.Views.Pages;
using Everywhere.Web;
using Microsoft.Extensions.Logging;
using Pure.DI;
using Pure.DI.MS;
using ShadUI;
using static Pure.DI.Lifetime;

namespace Everywhere.DependencyInjection;

#pragma warning disable CA1416

// Project DI convention:
// - Bind application services in the business-focused partial setup files.
// - Root exports a service to the final Microsoft IServiceProvider; Pure.DI.MS
//   registers by contract type, tag, and lifetime, so roots stay anonymous unless
//   code directly calls a generated composition member.
// - OnNewRoot is required by Pure.DI.MS to add roots to IServiceCollection.
// - OnCannotResolve is reserved for framework services owned by MS DI.
public partial class CoreComposition : ServiceProviderFactory<CoreComposition>
{
    private const RootKinds ExportedRoot = RootKinds.Default | RootKinds.Exported;

    // ReSharper disable once UnusedMember.Local
    private static void SetupCoreServices() =>
        DI.Setup()
            // Pure.DI.MS hooks and framework fallbacks.
            .Hint(Hint.OnCannotResolve, "On")
            .Hint(Hint.OnCannotResolvePartial, "Off")
            .Hint(Hint.OnNewRoot, "On")
            .Hint(Hint.OnNewRootPartial, "Off")
            .Hint(Hint.OnCannotResolveContractTypeNameWildcard, "Microsoft.Extensions.*")
            .Hint(Hint.OnCannotResolveContractTypeNameWildcard, "Microsoft.EntityFrameworkCore.*")
            .Hint(Hint.OnCannotResolveContractTypeNameWildcard, "System.Net.Http.*")

            // Logging facade instances are created by Pure.DI; ILoggerFactory stays in MS DI.
            .Bind<ILogger<TT>>().As(Singleton).To<Logger<TT>>()

            // Root service provider and settings roots.
            .Root<IServiceProvider>(kind: ExportedRoot)
            .Root<Settings>(kind: ExportedRoot)
            .Root<SettingsEngine>(kind: ExportedRoot)
            .Root<SoftwareUpdateControl>(kind: ExportedRoot)
#if WINDOWS
            .Root<RestartAsAdministratorControl>(kind: ExportedRoot)
#endif
            .Root<OpenWebBrowserControl>(kind: ExportedRoot)
            .Root<DebugFeaturesControl>(kind: ExportedRoot)
            .Root<CustomAssistantInitializer>(kind: ExportedRoot)
            .Root<PersistentKeyValueStorage>(kind: ExportedRoot)
            .Root<IKeyValueStorage>(kind: ExportedRoot)
            .Root<PersistentState>(kind: ExportedRoot)

            // Network and runtime roots.
            .Root<DynamicWebProxy>(kind: ExportedRoot)
            .Root<IWebProxy>(kind: ExportedRoot)
            .Root<FileDownloadService>(kind: ExportedRoot)
            .Root<IFileDownloadService>(kind: ExportedRoot)
            .Root<RuntimeManager>(kind: ExportedRoot)
            .Root<IRuntimeManager>(kind: ExportedRoot)
            .Root<NetworkInitializer>(kind: ExportedRoot)

            // Storage, prompt, notification, and statistics roots.
            .Root<IDefaultPromptProvider>(kind: ExportedRoot)
            .Root<IPromptService>(kind: ExportedRoot)
            .Root<IAssistantPromptResolver>(kind: ExportedRoot)
            .Root<IAssistantPromptReferenceService>(kind: ExportedRoot)
            .Root<IBlobStorage>(kind: ExportedRoot)
            .Root<IChatContextStorage>(kind: ExportedRoot)
            .Root<NotificationCenter>(kind: ExportedRoot)
            .Root<INotificationCenter>(kind: ExportedRoot)
            .Root<IStatisticsRecorder>(kind: ExportedRoot)
            .Root<IStatisticsService>(kind: ExportedRoot)
            .Root<ChatDbInitializer>(kind: ExportedRoot)
            .Root<PromptDbInitializer>(kind: ExportedRoot)
            .Root<StatisticsDbInitializer>(kind: ExportedRoot)
            .Root<StatisticsBackfiller>(kind: ExportedRoot)

            // Avalonia host, shell, and effect roots.
            .Root<DialogManager>(kind: ExportedRoot)
            .Root<ToastHost>(kind: ExportedRoot)
            .Root<VisualTreeDebugger>(kind: ExportedRoot)
            .Root<ChatWindowViewModel>(kind: ExportedRoot)
            .Root<ChatWindow>(kind: ExportedRoot)
            .Root<IVisualElementAnimationTarget>(kind: ExportedRoot)

            // Navigation page roots.
            .Root<HomePageViewModel>(kind: ExportedRoot)
            .Root<HomePage>(kind: ExportedRoot)
            .Root<CustomAssistantPageViewModel>(kind: ExportedRoot)
            .Root<CustomAssistantPage>(kind: ExportedRoot)
            .Root<PromptPageViewModel>(kind: ExportedRoot)
            .Root<PromptPage>(kind: ExportedRoot)
            .Root<PromptEditorViewModel>(kind: ExportedRoot)
            .Root<PromptEditorPage>(kind: ExportedRoot)
            .Root<ChatPluginPageViewModel>(kind: ExportedRoot)
            .Root<ChatPluginPage>(kind: ExportedRoot)
            .Root<SkillPageViewModel>(kind: ExportedRoot)
            .Root<SkillPage>(kind: ExportedRoot)
            .Root<WebSearchEnginePageViewModel>(kind: ExportedRoot)
            .Root<WebSearchEnginePage>(kind: ExportedRoot)
            .Root<SettingsPage>(kind: ExportedRoot)

            // Secondary view roots.
            .Root<WelcomeViewModel>(kind: ExportedRoot)
            .Root<WelcomeView>(kind: ExportedRoot)
            .Root<ChangeLogViewModel>(kind: ExportedRoot)
            .Root<ChangeLogView>(kind: ExportedRoot)
            .Root<MainViewModel>(kind: ExportedRoot)
            .Root<MainView>(kind: ExportedRoot)
            .Root<VisualElementEffect>(kind: ExportedRoot)

            // Chat, skill, and browser interaction roots.
            .Root<IKernelMixinFactory>(kind: ExportedRoot)
            .Root<SkillSource>(kind: ExportedRoot)
            .Root<SkillManager>(kind: ExportedRoot)
            .Root<ISkillManager>(kind: ExportedRoot)
            .Root<ISkillPromptProvider>(kind: ExportedRoot)
            .Root<IChatWindowNotificationService>(kind: ExportedRoot)
            .Root<IChatService>(kind: ExportedRoot)
            .Root<IGreetings>(kind: ExportedRoot)
            .Root<IWebBrowserHost>(kind: ExportedRoot)
            .Root<ChatContextManager>(kind: ExportedRoot)
            .Root<IChatContextManager>(kind: ExportedRoot)

            // Interop and strategy engine roots.
            .Root<WatchdogManager>(kind: ExportedRoot)
            .Root<IWatchdogManager>(kind: ExportedRoot)
            .Root<IStrategyRegistry>(kind: ExportedRoot)
            .Root<IStrategyEngine>(kind: ExportedRoot)

            // Built-in chat plugin concrete roots.
            .Root<EssentialPlugin>(kind: ExportedRoot)
            .Root<VisualContextPlugin>(kind: ExportedRoot)
            .Root<FileSystemPlugin>(kind: ExportedRoot)
            .Root<WebPlugin>(kind: ExportedRoot)
            .Root<TerminalPlugin>(kind: ExportedRoot)

            // Pure.DI aggregate consumed through the final MS provider.
            .Root<IEnumerable<IMainViewNavigationItem>>(kind: ExportedRoot);
}