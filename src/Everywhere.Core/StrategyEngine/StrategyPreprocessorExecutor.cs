using Everywhere.I18N;

namespace Everywhere.StrategyEngine;

public sealed class StrategyPreprocessorExecutor(IStrategyPreprocessorRegistry registry) : IStrategyPreprocessorExecutor
{
    public async Task<StrategyPreprocessorExecutionResult> ExecuteAsync(
        StrategyExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Strategy.Preprocessors.Count == 0)
        {
            return new StrategyPreprocessorExecutionResult();
        }

        var diagnostics = new List<StrategyDiagnostic>();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var id in context.Strategy.Preprocessors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!registry.TryGet(id, out var preprocessor))
            {
                diagnostics.Add(CreateDiagnostic(
                    StrategyDiagnosticSeverity.Error,
                    "preprocessor.not_found",
                    $"Strategy preprocessor '{id}' is not registered.",
                    id));
                break;
            }

            var result = await ExecuteOneAsync(preprocessor, context, diagnostics, cancellationToken);
            if (result is null)
            {
                break;
            }

            diagnostics.AddRange(result.Diagnostics);
            foreach (var diagnostic in result.Diagnostics)
            {
                if (diagnostic.Severity == StrategyDiagnosticSeverity.Error)
                {
                    return CreateResult(variables, diagnostics);
                }
            }

            foreach (var (key, value) in result.Variables ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    diagnostics.Add(CreateDiagnostic(
                        StrategyDiagnosticSeverity.Error,
                        "preprocessor.invalid_result",
                        $"Strategy preprocessor '{id}' returned an empty variable key.",
                        id));
                    return CreateResult(variables, diagnostics);
                }

                if (variables.ContainsKey(key))
                {
                    diagnostics.Add(CreateDiagnostic(
                        StrategyDiagnosticSeverity.Warning,
                        "preprocessor.variable_overridden",
                        $"Strategy preprocessor variable '{key}' was overridden by '{id}'.",
                        id));
                }

                variables[key] = value ?? string.Empty;
            }
        }

        return CreateResult(variables, diagnostics);
    }

    private static async Task<PreprocessorResult?> ExecuteOneAsync(
        IStrategyPreprocessor preprocessor,
        StrategyExecutionContext context,
        List<StrategyDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(context.Strategy.Options.PreprocessorTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            var result = await preprocessor.ProcessAsync(context with { CancellationToken = linkedSource.Token }, linkedSource.Token);
            if (result is null)
            {
                diagnostics.Add(CreateDiagnostic(
                    StrategyDiagnosticSeverity.Error,
                    "preprocessor.invalid_result",
                    $"Strategy preprocessor '{preprocessor.Id}' returned no result.",
                    preprocessor.Id));
            }

            return result;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            diagnostics.Add(CreateDiagnostic(
                StrategyDiagnosticSeverity.Error,
                "preprocessor.timeout",
                $"Strategy preprocessor '{preprocessor.Id}' timed out.",
                preprocessor.Id,
                ex,
                context.Strategy.Options.PreprocessorTimeout));
            return null;
        }
        catch (Exception ex)
        {
            diagnostics.Add(CreateDiagnostic(
                StrategyDiagnosticSeverity.Error,
                "preprocessor.exception",
                $"Strategy preprocessor '{preprocessor.Id}' failed: {ex.Message}",
                preprocessor.Id,
                ex));
            return null;
        }
    }

    private static StrategyPreprocessorExecutionResult CreateResult(
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyList<StrategyDiagnostic> diagnostics) =>
        new()
        {
            Result = new PreprocessorResult
            {
                Variables = variables.Count == 0 ?
                    null :
                    new Dictionary<string, string>(variables, StringComparer.Ordinal),
                Diagnostics = diagnostics
            },
            Diagnostics = diagnostics.ToArray()
        };

    private static StrategyDiagnostic CreateDiagnostic(
        StrategyDiagnosticSeverity severity,
        string code,
        string message,
        string path,
        Exception? exception = null,
        TimeSpan? duration = null) =>
        new()
        {
            Severity = severity,
            Code = code,
            MessageKey = new DirectResourceKey(message),
            Path = $"preprocessors.{path}",
            Duration = duration,
            Exception = exception
        };
}
