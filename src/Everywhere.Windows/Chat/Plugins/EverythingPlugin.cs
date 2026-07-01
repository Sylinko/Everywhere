using System.ComponentModel;
using System.Text;
using DynamicData;
using EverythingNet.Core;
using EverythingNet.Interfaces;
using Everywhere.AI;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.I18N;
using Everywhere.Interop;
using Everywhere.Utilities;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Everywhere.Windows.Chat.Plugins;

/// <summary>
/// A plugin that integrates with the `Everything` search engine to provide file search capabilities within the chat application.
/// </summary>
public sealed class EverythingPlugin : BuiltInChatPlugin
{
    public override IDynamicLocaleKey HeaderKey { get; } = new DynamicLocaleKey(LocaleKey.Windows_BuiltInChatPlugin_Everything_Header);

    public override IDynamicLocaleKey DescriptionKey { get; } =
        new DynamicLocaleKey(LocaleKey.Windows_BuiltInChatPlugin_Everything_Description);

    public override LucideIconKind? Icon => LucideIconKind.Search;

    public override string BeautifulIcon => "avares://Everywhere/Assets/Icons/Everything.svg";

    private readonly SemaphoreSlim _asyncLock = new(1, 1);

    private readonly INativeHelper _nativeHelper;
    private readonly IWatchdogManager _watchdogManager;
    private readonly ILogger<EverythingPlugin> _logger;

    /// <summary>
    /// Kill the "Everything" process if no search request is made within the debounce period.
    /// </summary>
    /// <returns></returns>
    private readonly DebounceExecutor<EverythingPlugin, ThreadingTimerImpl> _everythingShutdownDebounce;

    public EverythingPlugin(INativeHelper nativeHelper, IWatchdogManager watchdogManager, ILogger<EverythingPlugin> logger) : base("everything")
    {
        _nativeHelper = nativeHelper;
        _watchdogManager = watchdogManager;
        _logger = logger;

        _everythingShutdownDebounce = new DebounceExecutor<EverythingPlugin, ThreadingTimerImpl>(
            () => this,
            that =>
            {
                using var _ = that._asyncLock.Lock();

                try
                {
                    if (EverythingState.Process is not { HasExited: false, Id: var pid }) return;

                    that._logger.LogInformation("Shutting down Everything process (PID: {Pid}) due to inactivity.", pid);

                    EverythingState.Exit();
                    that._watchdogManager.UnregisterProcessAsync(pid).Detach();
                }
                catch
                {
                    // ignore
                }
            },
            TimeSpan.FromMinutes(5));

        AppDomain.CurrentDomain.ProcessExit += delegate
        {
            try
            {
                EverythingState.Exit();
            }
            catch
            {
                // ignore
            }
        };

        _functionsSource.Add(
            new BuiltInChatFunction(
                SearchFilesAsync,
                ChatFunctionPermissions.FileRead));
    }

    private async ValueTask EnsureEverythingRunningAsync()
    {
        using var _ = await _asyncLock.LockAsync();

        _everythingShutdownDebounce.Trigger(); // reset the debounce timer

        if (EverythingState.IsStarted()) return;

        EverythingState.StartService(_nativeHelper.IsAdministrator, EverythingState.StartMode.Service);
        if (EverythingState.Process is { } process)
        {
            await _watchdogManager.RegisterProcessAsync(process.Id);
        }

        var maxAttempts = 5;
        do
        {
            await Task.Delay(300);
        }
        while (!EverythingState.IsReady() && maxAttempts-- > 0);
    }

    [KernelFunction("search_files")]
    [Description("Search files using Everything search engine.")]
    [DynamicLocaleKey(LocaleKey.Windows_BuiltInChatPlugin_Everything_SearchFiles_Header, LocaleKey.Windows_BuiltInChatPlugin_Everything_SearchFiles_Description)]
    private async Task<string> SearchFilesAsync(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [Description("Standard search pattern in Everything search engine.")] string searchPattern,
        [Description("Maximum number of results to return. Default is 50 and will be limited to 1000.")]
        int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing Everything search with pattern: {SearchPattern}, maxResults: {MaxResults}", searchPattern, maxResults);

        if (maxResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults), "maxResults must be greater than 0.");
        }

        await EnsureEverythingRunningAsync();

        return await Task.Run(
                () =>
                {
                    using var everything = new Everything();
                    var records = everything
                        .SendSearch(searchPattern, default)
                        .Take(Math.Min(maxResults, 1000))
                        .Select(CreateFileRecord)
                        .ToList();

                    displaySink.AppendDynamicLocaleKey(
                        new FormattedDynamicLocaleKey(
                            LocaleKey.Windows_BuiltInChatPlugin_Everything_SearchFiles_DetailMessage,
                            new DirectLocaleKey(everything.Count.ToString())));

                    if (records.Count == 0) return "No results found.";

                    // Iterative OmitTo: consume remaining budget per record, stop when exhausted.
                    // File records are similarly-sized so proportional QoS allocation is unnecessary.
                    var sb = new StringBuilder();
                    sb.Append("Count: ").Append(everything.Count).AppendLine();
                    sb.AppendLine(FileRecord.Header).AppendLine("----");

                    const int totalBudget = 40000;
                    var remaining = totalBudget - TokenHelper.EstimateTokenCount(sb.ToString());
                    var included = 0;

                    foreach (var record in records)
                    {
                        remaining -= TokenHelper.OmitTo(record.ToString(), sb, remaining, position: TokenHelper.OmitPosition.End) + 1;
                        sb.AppendLine();

                        included++;

                        if (remaining <= 0) break;
                    }

                    var omitted = records.Count - included;
                    if (omitted > 0)
                        sb.Append("... ").Append(omitted).AppendLine(" more result(s) (omitted due to token budget)");

                    return sb.ToString();
                },
                cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        static FileRecord CreateFileRecord(ISearchResult result)
        {
            var fullPath = result.FullPath;
            var bytesSize = result.IsFile ? result.Size : -1;

            try
            {
                var created = result.Created;
                var modified = result.Modified;
                var attributes = (int)result.Attributes == -1 ? FileAttributes.None : (FileAttributes)result.Attributes;
                return new FileRecord(fullPath, bytesSize, created, modified, attributes);
            }
            catch
            {
                // use default values if any exception occurs
                return new FileRecord(fullPath, bytesSize, null, null, FileAttributes.None);
            }
        }
    }
}