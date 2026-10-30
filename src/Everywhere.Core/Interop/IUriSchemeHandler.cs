namespace Everywhere.Interop;

/// <summary>
/// Prepares an application-owned URI callback route before external authorization.
/// Platforms with installer-managed routes do not need to expose this capability.
/// </summary>
public interface IUriSchemeHandler
{
    /// <summary>
    /// Registers the callback scheme for this running installation.
    /// </summary>
    Task<bool> EnsureUriSchemeHandlerAsync(Uri callbackUri);
}