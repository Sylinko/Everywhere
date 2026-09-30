using Everywhere.Interop;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Activation;

/// <summary>Provides early endpoint ownership and forwarding for platforms that opt into RPC activation.</summary>
public static class ApplicationActivationEntrance
{
    private static string ActivationEndpoint => ProcessRoleNames.GetApplicationActivationEndpoint(RpcRuntimeIdentity.GetDesktopSessionId());

    /// <summary>
    /// Claims Main's activation endpoint and queues an initial command-line URL before runtime initialization.
    /// Null means this process must take its platform's secondary path. The caller retains the server through
    /// application and DI cleanup; DI may consume the existing instance but does not own its disposal.
    /// </summary>
    public static ApplicationActivationServer? TryCreateServer(string[] args, INamedPipePeerVerifier peerVerifier)
    {
        var server = ApplicationActivationServer.TryCreate(ActivationEndpoint, peerVerifier);
        if (server is null) return null;

        if (CreateRequest(args) is UrlCallbackActivationRequest request) server.Enqueue(request);
        return server;
    }

    /// <summary>
    /// Forwards a command-line URL or a chat-window request to the existing Main and returns the secondary exit code.
    /// The platform entry point decides whether this launch should activate Main before calling this method.
    /// </summary>
    public static async Task<int> SendToPrimaryAsync(string[] args, INamedPipePeerVerifier peerVerifier)
    {
        try
        {
            var status = await ApplicationActivationClient.SendAsync(ActivationEndpoint, CreateRequest(args), peerVerifier).ConfigureAwait(false);
            if (status == ApplicationActivationStatus.Accepted) return 0;
            await Console.Error.WriteLineAsync($"Application activation was rejected: {status}.");
        }
        catch (Exception exception)
        {
            // The secondary process never initializes logging. Keep diagnostics useful without
            // including an OAuth URL or a remote exception message that may contain credentials.
            await Console.Error.WriteLineAsync($"Application activation failed: {exception.GetType().Name}.");
        }

        NativeMessageBox.Show(
            LocaleResolver.Common_Info,
            LocaleResolver.Entrance_ActivationFailed,
            NativeMessageBoxButtons.Ok,
            NativeMessageBoxIcon.Warning);
        return 1;
    }

    private static ApplicationActivationRequest CreateRequest(string[] args)
    {
        var callbackUrl = args.FirstOrDefault(x => x.StartsWith($"{UrlCallbackActivationRequest.UrlScheme}:", StringComparison.OrdinalIgnoreCase));
        return callbackUrl is null ? new ShowChatWindowActivationRequest() : new UrlCallbackActivationRequest(callbackUrl);
    }
}