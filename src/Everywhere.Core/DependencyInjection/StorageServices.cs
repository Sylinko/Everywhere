using Everywhere.AI.Prompts;
using Everywhere.AI.Prompts.Database;
using Everywhere.Common.Notification;
using Everywhere.Database;
using Everywhere.Statistics;
using Everywhere.Storage;
using Pure.DI;
using static Pure.DI.Lifetime;

namespace Everywhere.DependencyInjection;

public partial class CoreComposition
{
    // ReSharper disable once UnusedMember.Local
    private static void SetupStorageServices() =>
        DI.Setup()
            // Prompt storage and resolution.
            .Bind<IDefaultPromptProvider>().As(Singleton).To<DefaultPromptProvider>()
            .Bind<IPromptService>().As(Singleton).To<PromptService>()
            .Bind<IAssistantPromptResolver>().As(Singleton).To<AssistantPromptResolver>()
            .Bind<IAssistantPromptReferenceService>().As(Singleton).To<AssistantPromptReferenceService>()

            // Blob and chat context storage.
            .Bind<IBlobStorage>().As(Singleton).To<BlobStorage>()
            .Bind<IChatContextStorage>().As(Singleton).To<ChatContextStorage>()

            // Notifications and statistics.
            .Bind<NotificationCenter>().Bind<INotificationCenter>().As(Singleton).To<NotificationCenter>()
            .Bind<INotificationPublisher<TT>>().As(Singleton).To<NotificationPublisher<TT>>()
            .Bind<IStatisticsRecorder>().As(Singleton).To<StatisticsRecorder>()
            .Bind<IStatisticsService>().As(Singleton).To<StatisticsService>()

            // Database initialization.
            .Bind<ChatDbInitializer>().To<ChatDbInitializer>()
            .Bind<PromptDbInitializer>().To<PromptDbInitializer>()
            .Bind<StatisticsDbInitializer>().To<StatisticsDbInitializer>()
            .Bind<StatisticsBackfiller>().To<StatisticsBackfiller>();
}