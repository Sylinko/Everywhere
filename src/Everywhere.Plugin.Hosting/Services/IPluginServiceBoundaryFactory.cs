using System.Runtime.Loader;

namespace Everywhere.Plugin.Hosting.Services;

/// <summary>
/// Connects the engine to application-owned scope creation without publishing the root container
/// itself as a normal service available through IPluginContext.Services.
/// </summary>
internal interface IPluginServiceBoundaryFactory
{
    /// <summary>
    /// Creates the service scope owned by one activation of the prepared entry assembly.
    /// </summary>
    PluginServiceBoundary CreateBoundary(AssemblyLoadContext context);
}