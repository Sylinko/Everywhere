using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;

namespace Everywhere.Views;

/// <summary>Lazily created diagnostic surface with explicit traversal and text budgets.</summary>
public sealed partial class ChatErrorDetailsView : UserControl
{
    /// <summary>Gets bounded diagnostic text suitable for selection and copying.</summary>
    public string Details { get; }

    /// <summary>Creates details only when the user requests them.</summary>
    public ChatErrorDetailsView(Exception error)
    {
        Details = FormatDetails(error);
        InitializeComponent();
    }

    [RelayCommand]
    private Task CopyDetailsAsync() => App.Clipboard.SetTextAsync(Details);

    private static string FormatDetails(Exception error)
    {
        const int maximumLength = 65536;
        var text = new StringBuilder();
        var pending = new Stack<(Exception Error, int Depth)>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        if (error is ChatRequestException request)
        {
            text.AppendLine($"Failures: {request.TotalFailureCount}; retained: {request.Failures.Count}");
            foreach (var failure in request.Failures.Reverse())
                pending.Push((failure.Exception, 0));
            foreach (var failure in request.Failures)
                text.AppendLine($"Attempt {failure.AttemptNumber}: {failure.FailedAt:O}");
        }
        else pending.Push((error, 0));

        var count = 0;
        while (pending.Count > 0 && count < 48 && text.Length < maximumLength)
        {
            var (current, depth) = pending.Pop();
            if (!visited.Add(current)) continue;
            count++;
            Append(current.GetType().FullName);
            Append(current.Message);
            if (current is HandledChatException chat)
            {
                Append($"Category: {chat.GetType().FullName}; recovery: {chat.Recovery}");
                if (chat.Diagnostics is { } diagnostics)
                {
                    Append($"Rule: {diagnostics.RuleId}; source: {diagnostics.Source}; model: {diagnostics.ModelId}");
                }
                if (chat.Diagnostics?.Evidence is { } evidence)
                {
                    Append($"HTTP: {(int?)evidence.StatusCode}; gateway: {(int?)evidence.GatewayStatusCode}; code: {evidence.ErrorCode}; type: {evidence.ErrorType}");
                    Append($"Request ID: {evidence.RequestId}; retry-after: {evidence.RetryAfter}; timeout: {evidence.TimeoutPhase}");
                    if (evidence.FinishReason is not null || evidence.ProviderFinishReason is not null)
                        Append($"Finish reason: {evidence.FinishReason}; provider finish reason: {evidence.ProviderFinishReason}");
                    Append(evidence.ResponseBody);
                }
            }
            Append(current.StackTrace);
            if (depth >= 8) { Append("[Cause depth limit reached]"); continue; }
            if (current is AggregateException aggregate)
            {
                foreach (var cause in aggregate.InnerExceptions.Take(48 - count).Reverse()) pending.Push((cause, depth + 1));
            }
            else if (current.InnerException is { } inner) pending.Push((inner, depth + 1));
        }
        if (pending.Count > 0 || text.Length >= maximumLength) text.AppendLine("[Diagnostic output truncated]");
        return text.ToString();

        void Append(string? value)
        {
            if (string.IsNullOrEmpty(value) || text.Length >= maximumLength) return;
            text.Append(value.AsSpan(0, Math.Min(value.Length, maximumLength - text.Length)));
            text.AppendLine();
        }
    }
}
