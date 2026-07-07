using Everywhere.Database;
using Microsoft.Extensions.Logging;
using Pure.DI;
using Pure.DI.MS;
using static Pure.DI.Lifetime;

namespace Everywhere.Cloud.DependencyInjection;

public partial class CloudComposition : ServiceProviderFactory<CloudComposition>
{
    // ReSharper disable once UnusedMember.Local
    private static void SetupCloudServices() =>
        DI.Setup()
            // Pure.DI.MS hooks and framework fallbacks.
            .Hint(Hint.OnCannotResolve, "On")
            .Hint(Hint.OnCannotResolvePartial, "Off")
            .Hint(Hint.OnNewRoot, "On")
            .Hint(Hint.OnNewRootPartial, "Off")
            .Hint(Hint.OnCannotResolveContractTypeNameWildcard, "Microsoft.Extensions.*")
            .Hint(Hint.OnCannotResolveContractTypeNameWildcard, "Microsoft.AspNetCore.*")
            .Hint(Hint.OnCannotResolveContractTypeNameWildcard, "Microsoft.Maui.*")
            .Hint(Hint.OnCannotResolveContractTypeNameWildcard, "Microsoft.EntityFrameworkCore.*")
            .Hint(Hint.OnCannotResolveContractTypeNameWildcard, "System.Net.Http.*")
            .Hint(Hint.OnCannotResolveContractTypeNameWildcard, "Everywhere.*")

            // Logging facade instances are created by Pure.DI; ILoggerFactory stays in MS DI.
            .Bind<ILogger<TT>>().As(Singleton).To<Logger<TT>>()

            // Cloud service implementations.
            .Bind<OAuthCloudClient>().Bind<ICloudClient>().As(Singleton).To<OAuthCloudClient>()
            .Bind<CloudChatDbSynchronizer>().Bind<IChatDbSynchronizer>().As(Singleton).To<CloudChatDbSynchronizer>()
            .Bind<OfficialModelProvider>().Bind<IOfficialModelProvider>().As(Singleton).To<OfficialModelProvider>()

            // Cloud roots exported to the final MS provider.
            .Root<OAuthCloudClient>(kind: RootKinds.Exported)
            .Root<ICloudClient>(kind: RootKinds.Exported)
            .Root<CloudChatDbSynchronizer>(kind: RootKinds.Exported)
            .Root<IChatDbSynchronizer>(kind: RootKinds.Exported)
            .Root<OfficialModelProvider>(kind: RootKinds.Exported)
            .Root<IOfficialModelProvider>(kind: RootKinds.Exported);
}
