using System.ComponentModel;
using System.Text;
using DynamicData;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Mods.Everything.I18N;
using Everywhere.Prompting;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Everywhere.Mods.Everything;

/// <summary>
/// Exposes Everything search under the mod.everything tool settings identity.
/// </summary>
public sealed class EverythingChatPlugin : ModChatPlugin
{
    /// <inheritdoc />
    public override IDynamicLocaleKey HeaderKey { get; } = new DynamicLocaleKey(LocaleKey.Everything_Header);

    /// <inheritdoc />
    public override IDynamicLocaleKey DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.Everything_Description);

    /// <inheritdoc />
    public override LucideIconKind? Icon => LucideIconKind.Search;

    /// <inheritdoc />
    public override string? BeautifulIcon
    {
        get
        {
            using var stream = typeof(EverythingChatPlugin).Assembly.GetManifestResourceStream("Everywhere.Mods.Everything.Assets.Everything.svg");
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(reader.ReadToEnd()));
        }
    }

    /// <inheritdoc />
    public override IReadOnlyList<SettingsItem> SettingsItems => _settings.SettingsItems;

    private readonly EverythingConnection _connection;
    private readonly EverythingSettings _settings;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a fixed tool set; native connections and processes are opened only when a search is requested.
    /// </summary>
    public EverythingChatPlugin(
        EverythingConnection connection,
        EverythingSettings settings,
        ILogger logger) : base("everything")
    {
        _connection = connection;
        _settings = settings;
        _logger = logger;
        _functionsSource.Add(new NativeChatFunction(SearchFilesAsync, ChatFunctionPermissions.FileRead));
    }

    /// <summary>
    /// Searches the selected user's index or the mod-owned fallback and formats a bounded tool response.
    /// </summary>
    [KernelFunction("search_files")]
    [Description("Search files using Everything search engine.")]
    [DynamicLocaleKey(
        LocaleKey.Everything_SearchFiles_Header,
        LocaleKey.Everything_SearchFiles_Description)]
    private async Task<string> SearchFilesAsync(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [Description("Standard search pattern in Everything search engine.")] string searchPattern,
        [Description("Maximum number of results to return. Default is 50 and will be limited to 1000.")]
        int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing Everything search: {SearchPattern}; limit {MaxResults}.", searchPattern, maxResults);

        if (maxResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults), "maxResults must be greater than 0.");
        }

        var response = await _connection.SearchAsync(searchPattern, Math.Min(maxResults, 1000), cancellationToken);
        var records = response.Results.Select(result => new FileRecord(
            result.FullPath,
            result.IsFile ? result.Size : -1,
            result.Created,
            result.Modified,
            result.Attributes)).ToArray();

        displaySink.AppendDynamicLocaleKey(
            new FormattedDynamicLocaleKey(
                LocaleKey.Everything_SearchFiles_DetailMessage,
                new DirectLocaleKey(response.TotalCount.ToString())));

        if (records.Length == 0) return "No results found.";

        // Iterative OmitTo: consume remaining budget per record, stop when exhausted.
        // File records are similarly-sized so proportional QoS allocation is unnecessary.
        var sb = new StringBuilder();
        sb.Append("Count: ").Append(response.TotalCount).AppendLine();
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

        var omitted = records.Length - included;
        if (omitted > 0) sb.Append("... ").Append(omitted).AppendLine(" more result(s) (omitted due to token budget)");

        return sb.ToString();
    }
}