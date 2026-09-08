using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Everywhere.Automation.TestApp;
using Everywhere.Chat;
using Everywhere.Windows.Automation;

namespace Everywhere.Automation.WebView.Probe;

internal sealed class WebViewProbeSession(ProbeOptions options) : IAsyncDisposable
{
    public int TargetCount => _context.TargetCount;

    public int RetainedTurnCount => _context.RetainedTurnCount;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WindowsVisualElementBackend _backend = new();
    private readonly VisualContext _context = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TestAppProcessController? _controller;
    private TestAppStatus? _status;
    private VisualTargetTurn? _persistentTurn;
    private int _queryIndex;
    private int _textReadIndex;
    private bool _isDisposed;

    public async Task<ProbeNavigationResult> NavigateAsync(string address, int? settleMilliseconds = null, CancellationToken cancellationToken = default)
    {
        var uri = ParseAddress(address);
        var settleDelay = settleMilliseconds is { } milliseconds ? TimeSpan.FromMilliseconds(milliseconds) : options.SettleDelay;
        if (settleDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(settleMilliseconds));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            var stopwatch = Stopwatch.StartNew();
            TestAppStatus status;
            if (_controller is null)
            {
                var started = await TestAppProcessController.StartAsync(WebViewTestAppPath.Executable, "real-web", 0, options.NavigationTimeout, cancellationToken, "--url", uri.AbsoluteUri);
                _controller = started.Controller;
                status = started.ReadyStatus;
            }
            else
            {
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(options.NavigationTimeout);
                status = await _controller.NavigateAsync(uri.AbsoluteUri, timeoutSource.Token);
            }

            await Task.Delay(settleDelay, cancellationToken);
            _status = status;
            var result = new ProbeNavigationResult(uri.AbsoluteUri, status.Address ?? uri.AbsoluteUri, stopwatch.Elapsed, status.ProcessId, status.Roots.Count, status.Revision);
            await AppendTranscriptAsync("navigate", new { address = uri.AbsoluteUri, settleMilliseconds = settleDelay.TotalMilliseconds }, result, null);
            return result;
        }
        catch (Exception exception)
        {
            await AppendTranscriptAsync("navigate", new { address }, null, exception.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProbeQueryResult> QueryAsync(string target, string directions, int offset, int? limit = null, int? targetTokenBudget = null, CancellationToken cancellationToken = default, bool shouldStartNewTurn = false)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            await EnsureStartedAsync(cancellationToken);
            var normalizedLimit = Math.Min(limit ?? options.Limit, VisualQueryRequest.MaximumLimit);
            var normalizedTokenBudget = targetTokenBudget ?? options.TargetTokenBudget;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(offset);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(normalizedLimit);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(normalizedTokenBudget);

            var stopwatch = Stopwatch.StartNew();
            if (shouldStartNewTurn)
            {
                _persistentTurn?.Complete();
                _persistentTurn = _context.BeginTurn();
            }
            // Persistent turns span calls and navigation; otherwise this call owns a temporary generation.
            using var temporaryTurn = _persistentTurn is null ? _context.BeginTurn() : null;
            using var retention = _context.CreateRetention();
            var visualTarget = ResolveTarget(target, retention);
            var (content, publishedTargetCount) = await new VisualQuery(_context).ExecuteAsync(
                visualTarget,
                new VisualQueryRequest { Directions = ParseDirections(directions), Offset = offset, Limit = normalizedLimit },
                VisualContextPromptOptions.Default with { TargetTokenBudget = normalizedTokenBudget },
                cancellationToken: cancellationToken);
            temporaryTurn?.Complete();

            var status = _status ?? throw new InvalidOperationException("The WebView TestApp has no current status.");
            var queryIndex = ++_queryIndex;
            var host = GetSafeFileName(new Uri(status.Address ?? options.Addresses[0].AbsoluteUri).Host);
            var outputPath = Path.Combine(options.OutputDirectory, $"{queryIndex:000}-{host}.visual-context.txt");
            await File.WriteAllTextAsync(outputPath, content, cancellationToken);
            var result = new ProbeQueryResult(target, directions, offset, normalizedLimit, normalizedTokenBudget, content, outputPath, stopwatch.Elapsed, publishedTargetCount, _context.TargetCount, _context.RetainedTurnCount);
            await AppendTranscriptAsync("query_visual", new { target, directions, offset, shouldStartNewTurn, limit = normalizedLimit, targetTokenBudget = normalizedTokenBudget }, new { result.OutputPath, result.Elapsed, result.PublishedTargetCount, result.RetainedTargetCount, result.RetainedTurnCount, CharacterCount = content.Length }, null);
            return result;
        }
        catch (Exception exception)
        {
            await AppendTranscriptAsync("query_visual", new { target, directions, offset, limit, targetTokenBudget, shouldStartNewTurn }, null, exception.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProbeSessionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            var result = new ProbeSessionStatus(_controller is not null, _status?.Address, _status?.ProcessId, _status?.Roots.Select(static root => $"0x{root.NativeHandle:X}").ToArray() ?? [], _context.TargetCount, _context.RetainedTurnCount, _context.NextTargetId, options.OutputDirectory);
            await AppendTranscriptAsync("get_probe_status", null, result, null);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProbeTextReadResult> ReadTextAsync(int target, int offset = 0, int limit = VisualQuery.DefaultTextLimit, CancellationToken cancellationToken = default, bool shouldStartNewTurn = false)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            var stopwatch = Stopwatch.StartNew();
            if (shouldStartNewTurn)
            {
                _persistentTurn?.Complete();
                _persistentTurn = _context.BeginTurn();
            }
            using var temporaryTurn = _persistentTurn is null ? _context.BeginTurn() : null;
            var content = new VisualQuery(_context).ReadText(target, offset, limit);
            temporaryTurn?.Complete();

            var status = _status ?? throw new InvalidOperationException("The WebView TestApp has no current status.");
            var textReadIndex = ++_textReadIndex;
            var host = GetSafeFileName(new Uri(status.Address ?? options.Addresses[0].AbsoluteUri).Host);
            var outputPath = Path.Combine(options.OutputDirectory, $"{textReadIndex:000}-{host}.visual-text.txt");
            await File.WriteAllTextAsync(outputPath, content, cancellationToken);
            var result = new ProbeTextReadResult(target, offset, Math.Min(limit, VisualQuery.MaximumTextLimit), content, outputPath, stopwatch.Elapsed, _context.TargetCount, _context.RetainedTurnCount);
            await AppendTranscriptAsync("read_visual_text", new { target, offset, limit, shouldStartNewTurn }, new { result.OutputPath, result.Elapsed, result.RetainedTargetCount, result.RetainedTurnCount, CharacterCount = content.Length }, null);
            return result;
        }
        catch (Exception exception)
        {
            await AppendTranscriptAsync("read_visual_text", new { target, offset, limit, shouldStartNewTurn }, null, exception.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Saves a bounded native edge trace for the controlled window without publishing Agent targets.</summary>
    public async Task<string> DiagnoseTopologyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            await EnsureStartedAsync(cancellationToken);
            var status = _status ?? throw new InvalidOperationException("The WebView TestApp has no current status.");
            var records = TopologyProbe.Capture((nint)status.Roots.Single().NativeHandle);
            var content = JsonSerializer.Serialize(records, JsonOptions);
            await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "topology.json"), content, cancellationToken);
            return content;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        if (_controller is not null) await _controller.DisposeAsync();
        _context.Dispose();
        _persistentTurn = null;
        _backend.Dispose();
        _gate.Dispose();
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_controller is not null) return;
        var uri = options.Addresses[0];
        var started = await TestAppProcessController.StartAsync(WebViewTestAppPath.Executable, "real-web", 0, options.NavigationTimeout, cancellationToken, "--url", uri.AbsoluteUri);
        _controller = started.Controller;
        _status = started.ReadyStatus;
        await Task.Delay(options.SettleDelay, cancellationToken);
    }

    private VisualTarget ResolveTarget(string target, VisualElementRetention retention)
    {
        if (string.Equals(target, "root", StringComparison.OrdinalIgnoreCase))
        {
            var status = _status ?? throw new InvalidOperationException("The WebView TestApp has no current status.");
            var rootHandle = (nint)status.Roots.Single().NativeHandle;
            var root = _backend.Query(retention, VisualElementLocator.FromNativeWindow(rootHandle), VisualElementResolution.TopLevel) ?? throw new InvalidOperationException("The Windows reader did not resolve the native WebView probe window.");
            return new ElementTarget { Element = root.Element };
        }

        if (!int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) throw new KeyNotFoundException($"Visual target '{target}' is unavailable. Use 'root' or a decimal ID returned by query_visual.");
        return ResolveTarget(id);
    }

    private VisualTarget ResolveTarget(int target) => _context.TryGetTarget(target, out var visualTarget) ? visualTarget : throw new KeyNotFoundException($"Visual target '{target}' is unavailable. Query the current page and use an integer ID from that result.");

    private static VisualContextTraverseDirections ParseDirections(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return VisualContextTraverseDirections.All;
        var result = VisualContextTraverseDirections.Core;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result |= part.ToLowerInvariant() switch
            {
                "all" => VisualContextTraverseDirections.All,
                "none" or "core" => VisualContextTraverseDirections.Core,
                "parent" => VisualContextTraverseDirections.Parent,
                "child" or "children" => VisualContextTraverseDirections.Child,
                "previous" or "previoussibling" => VisualContextTraverseDirections.PreviousSibling,
                "next" or "nextsibling" => VisualContextTraverseDirections.NextSibling,
                "siblings" => VisualContextTraverseDirections.PreviousSibling | VisualContextTraverseDirections.NextSibling,
                _ => throw new ArgumentException($"Unknown visual traversal direction '{part}'.", nameof(value)),
            };
        }

        return result;
    }

    private async Task AppendTranscriptAsync(string operation, object? request, object? result, string? error)
    {
        var entry = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, operation, request, result, error }, JsonOptions);
        await File.AppendAllTextAsync(Path.Combine(options.OutputDirectory, "transcript.jsonl"), entry + Environment.NewLine);
    }

    private static Uri ParseAddress(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) throw new ArgumentException("The address must be an absolute HTTP or HTTPS URL.", nameof(address));
        return uri;
    }

    private static string GetSafeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalidCharacters.Contains(character) ? '-' : character).ToArray());
    }
}

internal sealed record ProbeNavigationResult(string RequestedAddress, string FinalAddress, TimeSpan Elapsed, int ProcessId, int RootCount, long Revision);

internal sealed record ProbeQueryResult(string Target, string Directions, int Offset, int Limit, int TargetTokenBudget, string Content, string OutputPath, TimeSpan Elapsed, int PublishedTargetCount, int RetainedTargetCount, int RetainedTurnCount);

internal sealed record ProbeTextReadResult(int Target, int Offset, int Limit, string Content, string OutputPath, TimeSpan Elapsed, int RetainedTargetCount, int RetainedTurnCount);

internal sealed record ProbeSessionStatus(bool IsStarted, string? Address, int? ProcessId, IReadOnlyList<string> RootHandles, int RetainedTargetCount, int RetainedTurnCount, int NextTargetId, string OutputDirectory);
