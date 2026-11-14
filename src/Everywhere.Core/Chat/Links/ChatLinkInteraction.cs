using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Views;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Chat.Links;

/// <summary>
/// Handles chat link commands and applies the chat settings to automatic previews.
/// Owns no window or preview cache; the initiating window supplies its UI context per command.
/// </summary>
public sealed class ChatLinkInteraction(
    ILinkFileOperations fileOperations,
    WebPreviewService previews,
    ChatWindowSettings settings,
    ILogger<ChatLinkInteraction> logger
)
{
    public static readonly AttachedProperty<ChatLinkInteraction?> InteractionProperty =
        AvaloniaProperty.RegisterAttached<ChatLinkInteraction, TopLevel, ChatLinkInteraction?>("Interaction");

    public bool IsPreviewEnabled => settings.IsAutomaticLinkPreviewEnabled;

    private static readonly HashSet<string> ExecutableExtensions =
    [
        with(StringComparer.OrdinalIgnoreCase),
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
        ".scr", ".cpl", ".msi", ".msp", ".msc", ".reg", ".lnk", ".url", ".pif", ".application", ".desktop", ".app",
        ".command", ".workflow", ".sh", ".bash", ".zsh", ".fish", ".py", ".pyw", ".pl", ".rb", ".jar", ".scpt", ".scptd",
    ];

    public static bool IsExecutableExtension(string path) => ExecutableExtensions.Contains(Path.GetExtension(path.TrimEnd(' ', '.')));

    public static bool IsPreviewable(Uri page) => WebPreviewService.CanPreviewPage(page);

    public async Task<WebPreview?> GetPreviewAsync(Uri page, CancellationToken cancellationToken, IProgress<WebPreview> progress)
    {
        if (!IsPreviewEnabled) return null;
        return await previews.GetAsync(page, cancellationToken, progress).ConfigureAwait(false);
    }

    public Task<byte[]?> GetSiteIconAsync(Uri page, CancellationToken cancellationToken) =>
        IsPreviewEnabled ? previews.GetSiteFaviconAsync(page, cancellationToken) : Task.FromResult<byte[]?>(null);

    public async Task ExecuteAsync(TopLevel owner, ChatLinkTarget target, ChatLinkAction action, string? label = null)
    {
        using var operation = new CancellationTokenSource();
        owner.PropertyChanged += HandleOwnerChanged;
        owner.Closed += HandleOwnerClosed;

        try
        {
            var status = await ExecuteCoreAsync(owner, target, action, label, operation.Token);
            if (operation.IsCancellationRequested) return;

            if (status == ChatLinkOperationStatus.ParentOpened)
            {
                GetToastHost(owner)?.CreateToast(LocaleKey.ChatLink_ParentOpened.I18N()).WithDurationSeconds(4).ShowInfo();
                return;
            }

            if (status is ChatLinkOperationStatus.Accepted or ChatLinkOperationStatus.Canceled) return;

            if (status == ChatLinkOperationStatus.Failed)
            {
                logger.LogDebug("The system rejected chat link action {Action} for {TargetKind}", action, target.Kind);
            }

            var message = status switch
            {
                ChatLinkOperationStatus.Unavailable when action == ChatLinkAction.OpenWith => LocaleKey.ChatLink_OpenWithUnavailable,
                ChatLinkOperationStatus.Unavailable => LocaleKey.ChatLink_ActionUnavailable,
                ChatLinkOperationStatus.NoAssociatedApplication when action == ChatLinkAction.OpenWith => LocaleKey.ChatLink_NoApplicationsAvailable,
                ChatLinkOperationStatus.NoAssociatedApplication when target.Kind == ChatLinkKind.Web => LocaleKey.ChatLink_NoBrowser,
                ChatLinkOperationStatus.NoAssociatedApplication => LocaleKey.ChatLink_NoAssociatedApplication,
                ChatLinkOperationStatus.MissingTarget => LocaleKey.ChatLink_MissingTarget,
                ChatLinkOperationStatus.RequiresFile => LocaleKey.ChatLink_RequiresFile,
                ChatLinkOperationStatus.SameFile => LocaleKey.ChatLink_SameFile,
                _ => GetFailureMessage(action, target.Kind)
            };
            ShowError(owner, message);
        }
        catch (OperationCanceledException)
        {
            // Dismissing a dialog is not an error.
        }
        catch (Exception exception) when
            (target.Kind == ChatLinkKind.Local &&
             action is ChatLinkAction.FollowLocalLink or ChatLinkAction.Reveal or ChatLinkAction.OpenWith &&
             exception is FileNotFoundException or DirectoryNotFoundException)
        {
            if (!operation.IsCancellationRequested) ShowError(owner, LocaleKey.ChatLink_MissingTarget);
        }
        catch (Exception exception)
        {
            // The exception message may contain the source, destination, or a full URL.
            var code = exception is Win32Exception native ? native.NativeErrorCode : exception.HResult;
            logger.LogWarning(
                "Chat link action {Action} failed for {TargetKind}: {ErrorType} ({ErrorCode})",
                action,
                target.Kind,
                exception.GetType().Name,
                code);
            if (operation.IsCancellationRequested) return;

            var isAccessDenied = exception is UnauthorizedAccessException ||
                OperatingSystem.IsWindows() && exception is Win32Exception { NativeErrorCode: 5 };
            ShowError(owner, GetFailureMessage(action, target.Kind), isAccessDenied ? LocaleKey.ChatLink_AccessDenied : null);
        }
        finally
        {
            owner.PropertyChanged -= HandleOwnerChanged;
            owner.Closed -= HandleOwnerClosed;
        }

        // ReSharper disable once AccessToDisposedClosure
        void HandleOwnerChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Visual.IsVisibleProperty && !owner.IsVisible) operation.Cancel();
        }

        // ReSharper disable once AccessToDisposedClosure
        void HandleOwnerClosed(object? sender, EventArgs args) => operation.Cancel();
    }

    private static ToastHost? GetToastHost(TopLevel owner)
    {
        var host = ToastManager.ResolveHost(owner);
        return host is not null && ReferenceEquals(TopLevel.GetTopLevel(host), owner) ? host : null;
    }

    private static void ShowError(TopLevel owner, string message, string? detail = null)
    {
        GetToastHost(owner)?.CreateToast(message.I18N(), detail?.I18N()).DismissOnClick().ShowError();
    }

    private static string GetFailureMessage(ChatLinkAction action, ChatLinkKind kind) => action switch
    {
        ChatLinkAction.FollowLocalLink or ChatLinkAction.Reveal => LocaleKey.ChatLink_RevealFailed,
        ChatLinkAction.SaveAs => LocaleKey.ChatLink_SaveFailed,
        ChatLinkAction.CopyAddress or ChatLinkAction.CopyText => LocaleKey.Common_CopyFailed,
        ChatLinkAction.OpenWith => LocaleKey.ChatLink_OpenWithFailed,
        _ when kind == ChatLinkKind.Web => LocaleKey.ChatLink_OpenWebFailed,
        _ => LocaleKey.ChatLink_OpenFileFailed
    };

    private async Task<ChatLinkOperationStatus> ExecuteCoreAsync(
        TopLevel owner,
        ChatLinkTarget target,
        ChatLinkAction action,
        string? label,
        CancellationToken cancellationToken)
    {
        if (action is ChatLinkAction.CopyAddress or ChatLinkAction.CopyText)
        {
            if (owner.Clipboard is not { } clipboard) return ChatLinkOperationStatus.Unavailable;
            await clipboard.SetTextAsync(action == ChatLinkAction.CopyText ? label ?? target.Address : target.Address);
            return ChatLinkOperationStatus.Accepted;
        }

        if (target.Kind == ChatLinkKind.Web && action == ChatLinkAction.Open)
        {
            return await StartDefaultAsync(target, cancellationToken);
        }

        if (target.Kind != ChatLinkKind.Local) return ChatLinkOperationStatus.Unavailable;

        var path = target.Address;
        (bool IsDirectory, bool IsExecutable) file;
        try
        {
            file = await Task.Run(
                () =>
                {
                    var isFolder = File.GetAttributes(path).HasFlag(FileAttributes.Directory);
                    var shouldCheckExecution = action == ChatLinkAction.Open || action == ChatLinkAction.OpenWith && !isFolder;
                    return (isFolder, shouldCheckExecution && IsExecutionCandidate(path, isFolder));
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return ChatLinkOperationStatus.MissingTarget;
        }

        switch (action)
        {
            case ChatLinkAction.FollowLocalLink when file.IsDirectory:
                return await fileOperations.OpenDirectoryAsync(path, owner, cancellationToken);
            case ChatLinkAction.FollowLocalLink:
            case ChatLinkAction.Reveal:
                var itemPath = Path.TrimEndingDirectorySeparator(path);
                if (file.IsDirectory && Path.GetDirectoryName(itemPath) is null) return ChatLinkOperationStatus.Unavailable;
                return await fileOperations.RevealItemAsync(itemPath, file.IsDirectory, owner, cancellationToken);
            case ChatLinkAction.SaveAs:
                return file.IsDirectory ? ChatLinkOperationStatus.RequiresFile : await SaveAsync(owner, path, cancellationToken);
            case ChatLinkAction.OpenWith when file.IsDirectory:
                return ChatLinkOperationStatus.RequiresFile;
        }

        if (file.IsExecutable)
        {
            var host = DialogManager.ResolveHost(owner);
            if (host is null || !ReferenceEquals(TopLevel.GetTopLevel(host), owner)) return ChatLinkOperationStatus.Unavailable;

            var result = await host.CreateDialog(
                    new TextBlock
                    {
                        Text = path,
                        TextWrapping = TextWrapping.Wrap
                    },
                    LocaleKey.ChatLink_ConfirmExecution.I18N())
                .WithPrimaryButton(
                    (action == ChatLinkAction.OpenWith ? LocaleKey.ChatLink_OpenWith : LocaleKey.ChatLink_Run).I18N(),
                    buttonStyle: ButtonStyle.Destructive)
                .WithCancelButton(LocaleKey.Common_Cancel.I18N())
                .ShowAsync(cancellationToken);
            if (result != DialogResult.Primary) return ChatLinkOperationStatus.Canceled;
        }

        if (action != ChatLinkAction.OpenWith) return await StartDefaultAsync(target, cancellationToken);

        return owner is ChatWindow chatWindow ?
            await chatWindow.RunLinkPickerAsync(() => fileOperations.OpenFileWithAsync(path, owner, cancellationToken)) :
            await fileOperations.OpenFileWithAsync(path, owner, cancellationToken);
    }

    private static bool IsExecutionCandidate(string path, bool isDirectory)
    {
        if (IsExecutableExtension(path)) return true;
        if (isDirectory) return false;

        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
        {
            return true;
        }

        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[4];
        var count = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        return count >= 2 && (header[..2].SequenceEqual("MZ"u8) || header[..2].SequenceEqual("#!"u8)) ||
            count == 4 &&
            (header.SequenceEqual<byte>([0x7f, 0x45, 0x4c, 0x46]) ||
                header.SequenceEqual<byte>([0xcf, 0xfa, 0xed, 0xfe]) ||
                header.SequenceEqual<byte>([0xfe, 0xed, 0xfa, 0xcf]) ||
                header.SequenceEqual<byte>([0xca, 0xfe, 0xba, 0xbe]));
    }

    private async Task<ChatLinkOperationStatus> StartDefaultAsync(ChatLinkTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(target.Address) { UseShellExecute = true });
                return ChatLinkOperationStatus.Accepted;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is 1155 or unchecked((int)0x800401F5))
            {
                return ChatLinkOperationStatus.NoAssociatedApplication;
            }
            catch (Win32Exception exception) when (target.Kind == ChatLinkKind.Local && exception.NativeErrorCode is 2 or 3)
            {
                return ChatLinkOperationStatus.MissingTarget;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                return ChatLinkOperationStatus.Canceled;
            }
        }

        var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open") { UseShellExecute = false };
        start.ArgumentList.Add(target.Address);
        using var launcher = Process.Start(start);
        if (launcher is null) return ChatLinkOperationStatus.Failed;

        await launcher.WaitForExitAsync(cancellationToken);
        if (launcher.ExitCode == 0) return ChatLinkOperationStatus.Accepted;
        logger.LogWarning("The default system launcher failed for {TargetKind} with exit code {ExitCode}", target.Kind, launcher.ExitCode);
        return ChatLinkOperationStatus.Failed;
    }

    private static async Task<ChatLinkOperationStatus> SaveAsync(TopLevel owner, string source, CancellationToken cancellationToken)
    {
        if (!owner.StorageProvider.CanSave) return ChatLinkOperationStatus.Unavailable;
        var options = new FilePickerSaveOptions
        {
            Title = LocaleKey.ChatLink_SaveAs.I18N(),
            SuggestedFileName = Path.GetFileName(source),
            ShowOverwritePrompt = true
        };
        using var destination = owner is ChatWindow chatWindow ?
            await chatWindow.RunLinkPickerAsync(() => owner.StorageProvider.SaveFilePickerAsync(options)) :
            await owner.StorageProvider.SaveFilePickerAsync(options);
        if (destination is null) return ChatLinkOperationStatus.Canceled;

        if (destination.TryGetLocalPath() is not { } path) return ChatLinkOperationStatus.Unavailable;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(source, path, comparison)) return ChatLinkOperationStatus.SameFile;

        // Keep File.Copy's built-in same-file protection.
        await Task.Run(() => File.Copy(source, path, overwrite: true), cancellationToken);
        return ChatLinkOperationStatus.Accepted;
    }
}