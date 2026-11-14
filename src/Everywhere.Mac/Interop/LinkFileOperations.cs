using System.Diagnostics;
using AppKit;
using Avalonia.Controls;
using Everywhere.Chat.Links;
using Everywhere.Interop;
using Foundation;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Mac.Interop;

internal sealed class LinkFileOperations(ILogger<LinkFileOperations> logger) : ILinkFileOperations
{
    public Task<ChatLinkOperationStatus> OpenDirectoryAsync(string path, TopLevel owner, CancellationToken cancellationToken)
    {
        // Explicitly target Finder so revealing an app bundle cannot launch it.
        return RunOpenAsync(["-b", "com.apple.finder", "--", path], ChatLinkAction.Reveal, cancellationToken);
    }

    public Task<ChatLinkOperationStatus> RevealItemAsync(string path, bool isDirectory, TopLevel owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NSWorkspace.SharedWorkspace.SelectFile(path, Path.GetDirectoryName(path) ?? "/")
            ? ChatLinkOperationStatus.Accepted : ChatLinkOperationStatus.Failed);
    }

    public async Task<ChatLinkOperationStatus> OpenFileWithAsync(string path, TopLevel owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var file = NSUrl.FromFilename(path);
        var urls = NSWorkspace.SharedWorkspace.GetUrlsForApplications(file);
        var applications = new List<string>(urls.Length);
        foreach (var url in urls)
        {
            using (url)
            {
                if (url.Path is { } applicationPath) applications.Add(applicationPath);
            }
        }

        if (applications.Count == 0)
        {
            return ChatLinkOperationStatus.NoAssociatedApplication;
        }

        var picker = new ListBox { MinWidth = 320, MaxHeight = 320 };
        foreach (var application in applications)
        {
            picker.Items.Add(Path.GetFileNameWithoutExtension(application));
        }
        picker.SelectedIndex = 0;

        var host = DialogManager.ResolveHost(owner);
        if (host is null || !ReferenceEquals(TopLevel.GetTopLevel(host), owner)) return ChatLinkOperationStatus.Unavailable;
        var choice = await host
            .CreateDialog(picker, CoreLocaleKey.ChatLink_OpenWith.I18N())
            .WithPrimaryButton(CoreLocaleKey.ChatLink_Open.I18N())
            .WithCancelButton(CoreLocaleKey.Common_Cancel.I18N())
            .ShowAsync(cancellationToken);
        if (choice == DialogResult.HostNotFound)
        {
            return ChatLinkOperationStatus.Unavailable;
        }

        if (choice != DialogResult.Primary || picker.SelectedIndex < 0)
        {
            return ChatLinkOperationStatus.Canceled;
        }

        return await RunOpenAsync(["-a", applications[picker.SelectedIndex], "--", path], ChatLinkAction.OpenWith, cancellationToken);
    }

    private async Task<ChatLinkOperationStatus> RunOpenAsync(string[] arguments, ChatLinkAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null) return ChatLinkOperationStatus.Failed;
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode == 0) return ChatLinkOperationStatus.Accepted;
        logger.LogWarning("open failed for chat link action {Action} with exit code {ExitCode}", action, process.ExitCode);
        return ChatLinkOperationStatus.Failed;
    }
}