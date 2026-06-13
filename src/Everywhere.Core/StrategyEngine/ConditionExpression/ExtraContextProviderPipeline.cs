namespace Everywhere.StrategyEngine.ConditionExpression;

/// <summary>
/// Synchronous bridge over extra-context providers used by the current Condition DSL runtime.
/// </summary>
/// <remarks>
/// M7/M8 evaluation remains synchronous, so the pipeline waits with the configured timeout and converts provider
/// failures into diagnostics instead of throwing through condition evaluation.
/// </remarks>
internal sealed class ExtraContextProviderPipeline(
    IEnumerable<IExtraContextProvider> providers,
    StrategyOptions options)
{
    private readonly IReadOnlyList<IExtraContextProvider> _providers = providers.ToArray();

    /// <summary>
    /// Attempts to collect a deferred <c>extra.*</c> root for the current strategy evaluation.
    /// </summary>
    /// <remarks>
    /// Provider roots are normalized so providers may advertise either <c>foo</c> or <c>extra.foo</c>. The first
    /// provider that can collect and returns a node wins.
    /// </remarks>
    public ExtraContextNode? Collect(
        string publicRoot,
        IReadOnlyList<string> requiredPaths,
        StrategyContext context,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics,
        string diagnosticPath)
    {
        var request = new ExtraContextRequest
        {
            PublicRoot = publicRoot,
            RequiredPaths = requiredPaths,
            Timeout = options.ExtraTimeout
        };
        var candidates = _providers
            .Where(provider => IsSameRoot(provider.PublicRoot, publicRoot))
            .ToArray();

        foreach (var provider in candidates)
        {
            if (!CanCollect(provider, context, request, diagnostics, diagnosticPath))
            {
                continue;
            }

            using var cts = new CancellationTokenSource(options.ExtraTimeout);
            try
            {
                var task = provider.CollectAsync(context, request, cts.Token);
                if (!task.Wait(options.ExtraTimeout))
                {
                    cts.Cancel();
                    AddDiagnostic(
                        diagnostics,
                        StrategyDiagnosticSeverity.Warning,
                        "extra.provider_timeout",
                        $"Extra context provider '{provider.Id}' timed out while collecting '{publicRoot}'.",
                        diagnosticPath,
                        provider.Id,
                        duration: options.ExtraTimeout);
                    return null;
                }

                var node = task.GetAwaiter().GetResult();
                if (node is not null)
                {
                    return node;
                }
            }
            catch (OperationCanceledException ex)
            {
                AddDiagnostic(
                    diagnostics,
                    StrategyDiagnosticSeverity.Warning,
                    "extra.provider_timeout",
                    $"Extra context provider '{provider.Id}' timed out while collecting '{publicRoot}'.",
                    diagnosticPath,
                    provider.Id,
                    ex,
                    options.ExtraTimeout);
                return null;
            }
            catch (Exception ex)
            {
                AddDiagnostic(
                    diagnostics,
                    StrategyDiagnosticSeverity.Warning,
                    "extra.provider_unavailable",
                    $"Extra context provider '{provider.Id}' failed while collecting '{publicRoot}'.",
                    diagnosticPath,
                    provider.Id,
                    ex);
                return null;
            }
        }

        AddDiagnostic(
            diagnostics,
            StrategyDiagnosticSeverity.Warning,
            "extra.provider_unavailable",
            $"No extra context provider is available for '{publicRoot}'.",
            diagnosticPath,
            source.ProviderId);
        return null;
    }

    private static bool CanCollect(
        IExtraContextProvider provider,
        StrategyContext context,
        ExtraContextRequest request,
        List<StrategyDiagnostic> diagnostics,
        string diagnosticPath)
    {
        try
        {
            return provider.CanCollect(context, request);
        }
        catch (Exception ex)
        {
            AddDiagnostic(
                diagnostics,
                StrategyDiagnosticSeverity.Warning,
                "extra.provider_unavailable",
                $"Extra context provider '{provider.Id}' cannot collect '{request.PublicRoot}'.",
                diagnosticPath,
                provider.Id,
                ex);
            return false;
        }
    }

    private static bool IsSameRoot(string providerRoot, string publicRoot) =>
        string.Equals(NormalizeRoot(providerRoot), NormalizeRoot(publicRoot), StringComparison.Ordinal);

    private static string NormalizeRoot(string root) =>
        root.StartsWith("extra.", StringComparison.Ordinal) ? root : $"extra.{root}";

    private static void AddDiagnostic(
        List<StrategyDiagnostic> diagnostics,
        StrategyDiagnosticSeverity severity,
        string code,
        string message,
        string path,
        string? providerId,
        Exception? exception = null,
        TimeSpan? duration = null) =>
        diagnostics.Add(new StrategyDiagnostic
        {
            Severity = severity,
            Code = code,
            MessageKey = new DirectResourceKey(message),
            Path = path,
            ProviderId = providerId,
            Exception = exception,
            Duration = duration
        });
}
