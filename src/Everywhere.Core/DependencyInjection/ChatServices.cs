using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Skills;
using Everywhere.Web;
using Pure.DI;
using static Pure.DI.Lifetime;

namespace Everywhere.DependencyInjection;

public partial class CoreComposition
{
    // ReSharper disable once UnusedMember.Local
    private static void SetupChatServices() =>
        DI.Setup()
            // Kernel mixins and skill services.
            .Bind<IKernelMixinFactory>().As(Singleton).To<KernelMixinFactory>()
            .Bind<SkillSource>().As(Singleton).To<SkillSource>()
            .Bind<SkillManager>().Bind<ISkillManager>().Bind<ISkillPromptProvider>().As(Singleton).To<SkillManager>()

            // Chat runtime services.
            .Bind<IChatWindowNotificationService>().As(Singleton).To<ChatWindowNotificationService>()
            .Bind<IChatService>().As(Singleton).To<ChatService>()

            // Chat-adjacent host helpers.
            .Bind<IGreetings>().As(Singleton).To<Greetings>()
            .Bind<IWebBrowserHost>().As(Singleton).To<WebBrowserHost>()

            // Chat context state.
            .Bind<ChatContextManager>().Bind<IChatContextManager>().As(Singleton).To<ChatContextManager>();
}