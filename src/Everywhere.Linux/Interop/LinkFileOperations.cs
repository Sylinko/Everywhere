using System.Threading.Channels;
using Avalonia.Controls;
using System.IO;
using Everywhere.Interop;
using Everywhere.Chat.Links;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Everywhere.Linux.Interop;

internal sealed class LinkFileOperations(ILogger<LinkFileOperations> logger) : ILinkFileOperations
{
    private const string Portal = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";

    public async Task<ChatLinkOperationStatus> RevealItemAsync(string path, bool isDirectory, TopLevel owner, CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await ConnectAsync(cancellationToken);
            if (connection is not null)
            {
                try
                {
                    await connection.CallMethodAsync(CreateShowItemsMessage(connection, path)).WaitAsync(cancellationToken);
                    return ChatLinkOperationStatus.Accepted;
                }
                catch (DBusErrorReplyException exception) when (IsUnavailable(exception))
                {
                    // File.OpenHandle cannot open a directory. Without ShowItems, reveal its parent instead.
                    if (!isDirectory)
                    {
                        var portalStatus = await OpenPortalAsync(connection, owner, path, shouldChooseApplication: false, cancellationToken);
                        if (portalStatus != ChatLinkOperationStatus.Unavailable) return portalStatus;
                    }
                }
            }
        }
        catch (DBusConnectFailedException)
        {
            // Without a session bus the file manager may still open a directory.
        }

        if (Path.GetDirectoryName(path) is not { } parent) return ChatLinkOperationStatus.Unavailable;
        var status = await OpenDirectoryAsync(parent, owner, cancellationToken);
        return status == ChatLinkOperationStatus.Accepted ? ChatLinkOperationStatus.ParentOpened : status;
    }

    public async Task<ChatLinkOperationStatus> OpenDirectoryAsync(string path, TopLevel owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new System.Diagnostics.ProcessStartInfo("xdg-open") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null) return ChatLinkOperationStatus.Failed;
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode == 0) return ChatLinkOperationStatus.Accepted;
        logger.LogWarning("xdg-open failed to reveal a chat link directory with exit code {ExitCode}", process.ExitCode);
        return ChatLinkOperationStatus.Failed;
    }

    public async Task<ChatLinkOperationStatus> OpenFileWithAsync(string path, TopLevel owner, CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await ConnectAsync(cancellationToken);
            return connection is null ? ChatLinkOperationStatus.Unavailable :
                await OpenPortalAsync(connection, owner, path, shouldChooseApplication: true, cancellationToken);
        }
        catch (DBusConnectFailedException)
        {
            return ChatLinkOperationStatus.Unavailable;
        }
    }

    private static async Task<DBusConnection?> ConnectAsync(CancellationToken cancellationToken)
    {
        if (DBusAddress.Session is not { } session) return null;
        var connection = new DBusConnection(session);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async Task<ChatLinkOperationStatus> OpenPortalAsync(
        DBusConnection connection,
        TopLevel owner,
        string path,
        bool shouldChooseApplication,
        CancellationToken cancellationToken)
    {
        var parentWindow = owner.TryGetPlatformHandle() is { HandleDescriptor: "XID" } handle
            ? $"x11:{handle.Handle:X}"
            : string.Empty;
        // Subscribe before the call. A response may precede its reply; buffer by returned request path,
        // including older portals that do not follow the modern handle_token path convention.
        var responses = Channel.CreateUnbounded<(string Path, uint Code)>();
        using var subscription = await connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Sender = Portal,
                Destination = connection.UniqueName,
                Interface = "org.freedesktop.portal.Request",
                Member = "Response",
                PathNamespace = "/org/freedesktop/portal/desktop/request"
            },
            static (message, _) => (message.PathAsString ?? string.Empty, message.GetBodyReader().ReadUInt32()),
            notification =>
            {
                if (notification.HasValue)
                {
                    responses.Writer.TryWrite(notification.Value);
                }
                else if (notification.IsCompletion)
                {
                    responses.Writer.TryComplete(notification.Exception);
                }
            },
            flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnOwnerChanged | ObserverFlags.EmitOnReaderFailed);
        var token = "everywhere_" + Guid.NewGuid().ToString("N");
        var sender = connection.UniqueName?.TrimStart(':').Replace('.', '_');
        var request = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";
        try
        {
            var version = await connection.CallMethodAsync(
                CreateVersionMessage(connection),
                static (message, _) => message.GetBodyReader().ReadVariantValue().GetUInt32()).WaitAsync(cancellationToken);
            if (version < 3)
            {
                return ChatLinkOperationStatus.Unavailable;
            }

            using var file = await Task.Run(
                () => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            request = await connection.CallMethodAsync(
                CreatePortalMessage(connection, file, parentWindow, shouldChooseApplication, token),
                static (message, _) => message.GetBodyReader().ReadObjectPathAsString()).WaitAsync(cancellationToken);

            while (true)
            {
                var response = await responses.Reader.ReadAsync(cancellationToken);
                if (response.Path != request)
                {
                    continue;
                }

                if (response.Code == 0) return ChatLinkOperationStatus.Accepted;
                if (response.Code == 1) return ChatLinkOperationStatus.Canceled;
                logger.LogWarning(
                    "The desktop portal failed chat link {Action} with response code {ResponseCode}",
                    shouldChooseApplication ? ChatLinkAction.OpenWith : ChatLinkAction.Reveal, response.Code);
                return ChatLinkOperationStatus.Failed;
            }
        }
        catch (DBusErrorReplyException exception) when (IsUnavailable(exception))
        {
            return ChatLinkOperationStatus.Unavailable;
        }
        catch (OperationCanceledException)
        {
            try
            {
                // Closing the request must outlive the cancellation that caused it.
                await connection.CallMethodAsync(CreateCloseMessage(connection, request)).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogDebug("Could not close the chat link portal request: {ErrorType} ({ErrorCode})",
                    exception.GetType().Name, exception.HResult);
            }

            throw;
        }
    }

    private static bool IsUnavailable(DBusErrorReplyException exception)
    {
        return exception.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner" or
            "org.freedesktop.DBus.Error.UnknownInterface" or "org.freedesktop.DBus.Error.UnknownMethod" or
            "org.freedesktop.portal.Error.NotFound" or "org.freedesktop.portal.Error.NotSupported";
    }

    private static MessageBuffer CreateShowItemsMessage(DBusConnection connection, string path)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: "org.freedesktop.FileManager1",
            path: "/org/freedesktop/FileManager1",
            @interface: "org.freedesktop.FileManager1",
            member: "ShowItems",
            signature: "ass");
        writer.WriteArray(new[] { ChatLinkTarget.ToFileUri(path) });
        writer.WriteString(string.Empty);
        return writer.CreateMessage();
    }

    private static MessageBuffer CreatePortalMessage(
        DBusConnection connection,
        Microsoft.Win32.SafeHandles.SafeFileHandle file,
        string parentWindow,
        bool shouldChooseApplication,
        string token)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: Portal,
            path: PortalPath,
            @interface: "org.freedesktop.portal.OpenURI",
            member: shouldChooseApplication ? "OpenFile" : "OpenDirectory",
            signature: "sha{sv}");
        writer.WriteString(parentWindow);
        writer.WriteHandle(file);
        var options = new Dictionary<string, VariantValue>
        {
            ["handle_token"] = token
        };
        if (shouldChooseApplication)
        {
            options["ask"] = true;
        }

        writer.WriteDictionary(options);
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateVersionMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: Portal,
            path: PortalPath,
            @interface: "org.freedesktop.DBus.Properties",
            member: "Get",
            signature: "ss");
        writer.WriteString("org.freedesktop.portal.OpenURI");
        writer.WriteString("version");
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateCloseMessage(DBusConnection connection, string path)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Portal, path: path, @interface: "org.freedesktop.portal.Request", member: "Close");
        return writer.CreateMessage();
    }
}