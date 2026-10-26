using Avalonia.Platform.Storage;
using Everywhere.Interop;
using Serilog;
using Tmds.DBus.Protocol;

namespace Everywhere.Linux.Platform;

/// <summary>
/// Opens external URIs through the desktop session. The portal launches its
/// configured application without inheriting Everywhere's private XDG profile.
/// Local file launches retain the platform fallback's association handling.
/// </summary>
public sealed partial class LinuxDesktopLauncher(ILauncher fallback) : ILauncher, IUriSchemeHandler
{
    private const string PortalService = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";

    /// <inheritdoc />
    public async Task<bool> LaunchUriAsync(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        if (uri.IsFile)
        {
            return await fallback.LaunchUriAsync(uri).ConfigureAwait(false);
        }

        if (DBusAddress.Session is not { } address)
        {
            return await fallback.LaunchUriAsync(uri).ConfigureAwait(false);
        }

        using var connection = new DBusConnection(address);
        try
        {
            return await OpenUriAsync(connection, uri).WaitAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException exception) when (
            exception.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.UnknownMethod")
        {
            return await fallback.LaunchUriAsync(uri).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Do not include the URI: authentication links contain flow state.
            Log.Warning("The desktop URI request failed ({FailureType}).", exception.GetType().Name);
            return false;
        }
    }

    /// <inheritdoc />
    public Task<bool> LaunchFileAsync(IStorageItem storageItem) => fallback.LaunchFileAsync(storageItem);

    private static async Task<bool> OpenUriAsync(DBusConnection connection, Uri uri)
    {
        await connection.ConnectAsync().ConfigureAwait(false);
        var response = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = "everywhere_" + Guid.NewGuid().ToString("N");
        var uniqueName = connection.UniqueName ?? throw new InvalidOperationException("The session bus did not assign a connection identity.");
        var requestPath = PortalPath + "/request/" + uniqueName.TrimStart(':').Replace('.', '_') + "/" + token;
        // Subscribe before sending. A fast default-handler response may arrive
        // before OpenURI returns its request handle. The caller's unique bus
        // identity and handle token determine that path before the call.
        using var watch = await connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Sender = PortalService,
                Path = requestPath,
                Interface = "org.freedesktop.portal.Request",
                Member = "Response"
            },
            static (message, _) => message.GetBodyReader().ReadUInt32(),
            notification =>
            {
                if (notification.HasValue)
                {
                    response.TrySetResult(notification.Value);
                }
                else if (notification.IsCompletion)
                {
                    response.TrySetException(notification.Exception);
                }
            },
            emitOnCapturedContext: false,
            flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed,
            state: null).ConfigureAwait(false);
        await connection.CallMethodAsync(CreateOpenRequest(connection, uri, token)).ConfigureAwait(false);
        return await response.Task.ConfigureAwait(false) == 0;
    }

    private static MessageBuffer CreateOpenRequest(DBusConnection connection, Uri uri, string token)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: PortalService,
            path: PortalPath,
            @interface: "org.freedesktop.portal.OpenURI",
            member: "OpenURI",
            signature: "ssa{sv}");
        writer.WriteString("");
        writer.WriteString(uri.AbsoluteUri);
        var options = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("handle_token");
        writer.WriteVariantString(token);
        writer.WriteDictionaryEnd(options);
        return writer.CreateMessage();
    }
}