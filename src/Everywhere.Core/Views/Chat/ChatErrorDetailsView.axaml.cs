using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.AI;

namespace Everywhere.Views;

/// <summary>
/// Lazily created diagnostic surface with explicit traversal and text budgets.
/// </summary>
public sealed partial class ChatErrorDetailsView : UserControl
{
    /// <summary>
    /// Gets bounded diagnostic text suitable for selection and copying.
    /// </summary>
    public string Details => FormatDetails(_error);

    /// <summary>
    /// Gets the localized explanation without resolving it before presentation.
    /// </summary>
    public IDynamicLocaleKey SummaryKey { get; }

    /// <summary>
    /// Gets the final classified error name, when available.
    /// </summary>
    public string? Category { get; }

    /// <summary>
    /// Gets the actual service status for the summary badge.
    /// </summary>
    public IDynamicLocaleKey? HttpStatusKey { get; }

    /// <summary>
    /// Gets the service's extracted message, rather than a synthesized explanation.
    /// </summary>
    public string? ServiceMessage { get; }

    /// <summary>
    /// Gets the final failure's diagnostic fields.
    /// </summary>
    public IReadOnlyList<ChatErrorDetailField> DiagnosticFields { get; }

    /// <summary>
    /// Gets bounded technical evidence and exception causes.
    /// </summary>
    public IReadOnlyList<ChatErrorDetailField> TechnicalFields { get; }

    /// <summary>
    /// Whether the summary contains any actual diagnostic badges.
    /// </summary>
    public bool HasSummaryBadges => Category is not null || HttpStatusKey is not null;

    private readonly Exception _error;

    /// <summary>
    /// Creates details only when the user requests them.
    /// </summary>
    public ChatErrorDetailsView(Exception error)
    {
        _error = error;
        var final = error is ChatRequestException request ? request.FinalFailure : error;
        var chat = final as HandledChatException;
        var evidence = chat?.Diagnostics?.Evidence;
        SummaryKey = (final as HandledException)?.FriendlyMessageKey ?? new DirectLocaleKey(final.Message);
        Category = chat?.GetType().Name;
        HttpStatusKey = evidence?.StatusCode is { } status ?
            new FormattedDynamicLocaleKey(LocaleKey.ChatErrorDetails_HttpStatus, new DirectLocaleKey(((int)status).ToString())) :
            null;
        ServiceMessage = string.IsNullOrWhiteSpace(evidence?.ErrorMessage) ? null : evidence.ErrorMessage;
        DiagnosticFields = CreateDiagnosticFields(chat);

        // The UI presents only the final failure. Retry history remains in copied diagnostics.
        TechnicalFields = [.. CreateTechnicalFields(chat), .. CreateExceptionFields(final)];
        InitializeComponent();
        DataContext = this;
    }

    [RelayCommand]
    private Task CopyDetailsAsync() => App.Clipboard.SetTextAsync(Details);

    private static string FormatDetails(Exception error)
    {
        const int maximumLength = 65536;
        var text = new StringBuilder();
        var pending = new Stack<(Exception Error, int Depth, string? Heading)>();
        if (error is ChatRequestException request)
        {
            text.AppendLine($"Failures: {request.TotalFailureCount}; retained: {request.Failures.Count}");
            foreach (var failure in request.Failures.AsValueEnumerable().Reverse())
            {
                pending.Push((failure.Exception, 0, $"Attempt {failure.AttemptNumber}: {failure.FailedAt:O}"));
            }
        }
        else
        {
            pending.Push((error, 0, null));
        }

        var count = 0;
        var visited = new HashSet<Exception>();
        while (pending.Count > 0 && count < 48 && text.Length < maximumLength)
        {
            var (current, depth, heading) = pending.Pop();
            if (!visited.Add(current)) continue;

            count++;
            Append(heading);
            Append($"Exception type: {current.GetType().FullName}");
            Append($"Cause depth: {depth}");
            Append($"Exception message: {current.Message}");
            if (current is HandledChatException chat)
            {
                foreach (var field in CreateDiagnosticFields(chat).Concat(CreateTechnicalFields(chat)))
                {
                    Append($"{field.Name}: {field.Value}");
                }
                if (chat.Diagnostics?.Evidence.ErrorMessage is { } serviceMessage)
                {
                    Append($"Service message: {serviceMessage}");
                }
            }
            if (current.StackTrace is { } trace)
            {
                Append($"Stack trace: {trace}");
            }
            if (depth >= 8)
            {
                Append("[Cause depth limit reached]");
                continue;
            }
            if (current is AggregateException aggregate)
            {
                foreach (var cause in aggregate.InnerExceptions.AsValueEnumerable().Take(48 - count).Reverse())
                {
                    pending.Push((cause, depth + 1, null));
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push((inner, depth + 1, null));
            }
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

    private static ChatErrorDetailField Field(string name, string labelKey, string value) =>
        new(name, new DynamicLocaleKey(labelKey), value);

    private static void AddField(List<ChatErrorDetailField> fields, string name, string labelKey, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        fields.Add(Field(name, labelKey, value));
    }

    private static List<ChatErrorDetailField> CreateDiagnosticFields(HandledChatException? chat)
    {
        var diagnostics = chat?.Diagnostics;
        var evidence = diagnostics?.Evidence;
        var fields = new List<ChatErrorDetailField>();
        AddField(fields, "Category", LocaleKey.ChatErrorDetails_Category, chat?.GetType().Name);
        AddField(fields, "HTTP status", LocaleKey.ChatErrorDetails_HttpStatusLabel, evidence?.StatusCode is { } status ? ((int)status).ToString() : null);
        AddField(fields, "Gateway HTTP status", LocaleKey.ChatErrorDetails_GatewayStatus, evidence?.GatewayStatusCode is { } gateway ? ((int)gateway).ToString() : null);
        AddField(fields, "Service code", LocaleKey.ChatErrorDetails_ServiceCode, evidence?.ErrorCode);
        AddField(fields, "Service type", LocaleKey.ChatErrorDetails_ServiceType, evidence?.ErrorType);
        AddField(fields, "Assistant API identifier", LocaleKey.ChatErrorDetails_AssistantApi, diagnostics?.ModelId);
        AddField(fields, "Request ID", LocaleKey.ChatErrorDetails_RequestId, evidence?.RequestId);
        AddField(fields, "Parameter", LocaleKey.ChatErrorDetails_Parameter, evidence?.Parameter);
        AddField(fields, "Finish reason", LocaleKey.ChatErrorDetails_FinishReason, evidence?.FinishReason);
        AddField(fields, "Provider finish reason", LocaleKey.ChatErrorDetails_ProviderFinishReason, evidence?.ProviderFinishReason);
        return fields;
    }

    private static IReadOnlyList<ChatErrorDetailField> CreateTechnicalFields(HandledChatException? chat)
    {
        var diagnostics = chat?.Diagnostics;
        var evidence = diagnostics?.Evidence;
        var fields = new List<ChatErrorDetailField>();
        AddField(fields, "Recovery advice", LocaleKey.ChatErrorDetails_Recovery, chat?.Recovery.GetType().Name);
        AddField(fields, "Rule", LocaleKey.ChatErrorDetails_Rule, diagnostics?.RuleId);
        AddField(fields, "Evidence source", LocaleKey.ChatErrorDetails_Source, diagnostics?.Source);
        AddField(fields, "Retry after", LocaleKey.ChatErrorDetails_RetryAfter, evidence?.RetryAfter?.ToString());
        AddField(fields, "Timeout phase", LocaleKey.ChatErrorDetails_Timeout, evidence is { TimeoutPhase: not ChatRequestTimeoutPhase.None } ? evidence.TimeoutPhase.ToString() : null);
        AddField(fields, "Socket error", LocaleKey.ChatErrorDetails_SocketError, evidence?.SocketError?.ToString());
        AddField(fields, "Connection code", LocaleKey.ChatErrorDetails_ConnectionCode, evidence?.ConnectionErrorCode);
        AddField(fields, "Response body", LocaleKey.ChatErrorDetails_ResponseBody, evidence?.ResponseBody);
        return fields;
    }

    private static List<ChatErrorDetailField> CreateExceptionFields(Exception error)
    {
        var fields = new List<ChatErrorDetailField>();
        var pending = new Stack<(Exception Error, int Depth)>();
        pending.Push((error, 0));

        var remaining = 65536;
        var hasTruncated = false;
        var visited = new HashSet<Exception>();
        while (pending.Count > 0 && visited.Count < 48 && remaining > 0)
        {
            var (current, depth) = pending.Pop();
            if (!visited.Add(current)) continue;

            Add("Exception type", LocaleKey.ChatErrorDetails_ExceptionType, current.GetType().FullName);
            Add("Exception message", LocaleKey.ChatErrorDetails_ExceptionMessage, current.Message);
            Add("Stack trace", LocaleKey.ChatErrorDetails_StackTrace, current.StackTrace);
            if (depth >= 8)
            {
                hasTruncated |= current.InnerException is not null;
                continue;
            }
            if (current is AggregateException aggregate)
            {
                foreach (var cause in aggregate.InnerExceptions.AsValueEnumerable().Take(48).Reverse())
                {
                    pending.Push((cause, depth + 1));
                }

                hasTruncated |= aggregate.InnerExceptions.Count > 48;
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push((inner, depth + 1));
            }
        }

        if (pending.Count > 0 || hasTruncated)
        {
            fields.Add(new ChatErrorDetailField("Truncated", new DynamicLocaleKey(LocaleKey.ChatErrorDetails_Truncated), string.Empty, true));
        }

        return fields;

        void Add(string name, string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            hasTruncated |= value.Length > remaining;
            var bounded = value[..Math.Min(value.Length, remaining)];
            remaining -= bounded.Length;
            if (bounded.Length > 0) fields.Add(Field(name, key, bounded));
        }
    }
}

/// <summary>
/// One diagnostic value with separate localized display and stable copy labels.
/// </summary>
public sealed record ChatErrorDetailField(string Name, IDynamicLocaleKey LabelKey, string Value, bool IsNotice = false);