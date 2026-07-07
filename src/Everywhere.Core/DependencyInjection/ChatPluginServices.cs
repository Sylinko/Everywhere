using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.BuiltIn;
using Pure.DI;
using static Pure.DI.Lifetime;

namespace Everywhere.DependencyInjection;

public partial class CoreComposition
{
    // ReSharper disable once UnusedMember.Local
    private static void SetupChatPluginServices() =>
        DI.Setup()
            // Core built-in chat plugins.
            .Bind<EssentialPlugin>().Bind<BuiltInChatPlugin>(Tag.Unique).As(Singleton).To<EssentialPlugin>()
            .Bind<VisualContextPlugin>().Bind<BuiltInChatPlugin>(Tag.Unique).As(Singleton).To<VisualContextPlugin>()
            .Bind<FileSystemPlugin>().Bind<BuiltInChatPlugin>(Tag.Unique).As(Singleton).To<FileSystemPlugin>()
            .Bind<WebPlugin>().Bind<BuiltInChatPlugin>(Tag.Unique).As(Singleton).To<WebPlugin>()
            .Bind<TerminalPlugin>().Bind<BuiltInChatPlugin>(Tag.Unique).As(Singleton).To<TerminalPlugin>();
}