using System.Runtime.Loader;

namespace Everywhere.Modding.Hosting.Services;

/// <summary>
/// Connects the engine to application-owned scope creation without publishing the root container
/// itself as a normal service available through IModContext.Services.
/// </summary>
internal interface IModServiceBoundaryFactory
{
    /// <summary>
    /// Creates the service scope owned by one activation of the prepared entry assembly.
    /// </summary>
    ModServiceBoundary CreateBoundary(AssemblyLoadContext context);
}