using System.Text;
using System.Text.Json.Serialization;
using MessagePack;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class AssistantChatMessage : ChatMessage, IHaveChatAttachments, IDisposable
{
    /// <inheritdoc />
    public override ChatMessageKind Kind => ChatMessageKind.Assistant;

    public override AuthorRole Role => AuthorRole.Assistant;

    [Key(0)]
#pragma warning disable CA1822
    // ReSharper disable once MemberCanBeMadeStatic.Local
    // for forward compatibility
    private string? LegacyContent => null;
#pragma warning restore CA1822

    [Key(1)]
    public IDynamicLocaleKey? ErrorMessageKey
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>Gets or sets the persisted user-stopped outcome.</summary>
    [Key(12)]
    public bool IsCanceled
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>Gets or sets runtime diagnostics; never serialized or included in prompts.</summary>
    [IgnoreMember]
    [JsonIgnore]
    public Exception? Error
    {
        get;
        set => SetProperty(ref field, value, isDurable: false);
    }

    [Key(2)]
    public override DateTimeOffset CreatedAt { get; }

    [Key(3)]
    public DateTimeOffset FinishedAt
    {
        get;
        set => SetProperty(ref field, value);
    }

    [IgnoreMember]
    [JsonIgnore]
    public double ElapsedSeconds => Math.Max((FinishedAt - CreatedAt).TotalSeconds, 0);

    [Key(4)]
#pragma warning disable CA1822
    // ReSharper disable once MemberCanBeMadeStatic.Local
    // for forward compatibility
    private IList<FunctionCallChatMessage>? LegacyFunctionCalls => null;
#pragma warning restore CA1822

    [Key(5)]

#pragma warning disable CA1822
    // ReSharper disable once MemberCanBeMadeStatic.Local
    // for forward compatibility
    private IEnumerable<LegacyAssistantChatMessageSpan>? LegacySerializableSpans => null;
#pragma warning restore CA1822

    /// <summary>
    /// Each span represents a part of the message content and function calls.
    /// </summary>
    [IgnoreMember]
    public IReadOnlyList<AssistantChatMessageSpan> Spans => Volatile.Read(ref _spans);

    [Key(9)]
    public MetadataDictionary Metadata
    {
        get;
        set => SetProperty(ref field, value);
    }

    [Key(10)]
    private IEnumerable<AssistantChatMessageSpan> SerializableSpans => Spans;

    [Key(11)]
    public ChatUsageDetails UsageDetails { get; }

    [IgnoreMember]
    public IEnumerable<ChatAttachment> Attachments => Spans.OfType<IHaveChatAttachments>().SelectMany(s => s.Attachments);

    /// <summary>
    /// Gets whether any span contains model output. Application-generated reset notices do not
    /// count as output. This does not indicate whether tool execution has completed.
    /// </summary>
    [IgnoreMember]
    [JsonIgnore]
    public bool HasOutputContent => Spans.AsValueEnumerable().Any(span => span.HasOutputContent);

    [IgnoreMember]
    public int Count => Spans.Count;

    [IgnoreMember]
    public IReadOnlyList<AssistantChatMessageSpan> Items => Spans;

    [IgnoreMember] private IReadOnlyList<AssistantChatMessageSpan> _spans;
    [IgnoreMember] private bool _isDisposed;

    [SerializationConstructor]
    private AssistantChatMessage(
        string? obsoletedContent,
        IDynamicLocaleKey? errorMessageKey,
        DateTimeOffset createdAt,
        DateTimeOffset finishedAt,
        IList<FunctionCallChatMessage>? legacyFunctionCalls,
        IEnumerable<LegacyAssistantChatMessageSpan>? legacySerializableSpans,
        MetadataDictionary metadata,
        IEnumerable<AssistantChatMessageSpan>? serializableSpans,
        ChatUsageDetails? usageDetails,
        bool isCanceled)
    {
        var spans = new List<AssistantChatMessageSpan>();
        if (!obsoletedContent.IsNullOrEmpty())
        {
            spans.Add(new AssistantChatMessageTextSpan(obsoletedContent));
        }

        ErrorMessageKey = errorMessageKey;
        IsCanceled = isCanceled;
        CreatedAt = createdAt;
        FinishedAt = finishedAt;
        Metadata = metadata;
        UsageDetails = usageDetails ?? new ChatUsageDetails();

        if (serializableSpans is not null)
        {
            spans = serializableSpans.ToList();
        }
        else
        {
            if (legacyFunctionCalls is { Count: > 0 })
            {
                spans.Add(new AssistantChatMessageFunctionCallSpan(legacyFunctionCalls));
            }
            if (legacySerializableSpans is not null)
            {
                spans.Clear();
                foreach (var legacySpan in legacySerializableSpans)
                {
                    if (legacySpan.ReasoningOutput is { Length: > 0 } reasoningOutput)
                    {
                        spans.Add(
                            new AssistantChatMessageReasoningSpan(reasoningOutput)
                            {
                                CreatedAt = legacySpan.CreatedAt,
                                FinishedAt = legacySpan.ReasoningFinishedAt ?? legacySpan.FinishedAt
                            });
                    }

                    if (legacySpan.FunctionCalls is { Count: > 0 } functionCalls)
                    {
                        spans.Add(
                            new AssistantChatMessageFunctionCallSpan(functionCalls)
                            {
                                CreatedAt = legacySpan.CreatedAt,
                                FinishedAt = legacySpan.FinishedAt
                            });
                    }

                    if (legacySpan.Content is { Length: > 0 } content)
                    {
                        spans.Add(
                            new AssistantChatMessageTextSpan(content)
                            {
                                CreatedAt = legacySpan.CreatedAt,
                                FinishedAt = legacySpan.FinishedAt
                            });
                    }
                }

            }
        }

        _spans = [.. spans];
    }

    public AssistantChatMessage() : this(
        obsoletedContent: null,
        errorMessageKey: null,
        createdAt: DateTimeOffset.UtcNow,
        finishedAt: default,
        legacyFunctionCalls: null,
        legacySerializableSpans: null,
        metadata: MetadataDictionary.Empty,
        serializableSpans: null,
        usageDetails: null,
        isCanceled: false)
    {
    }

    /// <inheritdoc />
    public override ChatAssistantState Capture()
    {
        var usage = UsageDetails;
        return new ChatAssistantState(
            IsBusy,
            CreatedAt,
            FinishedAt,
            ErrorMessageKey,
            Error,
            new ChatUsageState(
                usage.InputTokenCount,
                usage.OutputTokenCount,
                usage.TotalTokenCount,
                usage.TokensPerSecond,
                usage.CachedInputHitRate));
    }

    public void AddSpan(AssistantChatMessageSpan span)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        span.Owner = Owner;
        Volatile.Write(ref _spans, _spans.AsValueEnumerable().Append(span).ToArray());

        NotifyChanged();
    }

    /// <summary>
    /// Removes provisional output after a request checkpoint, retaining prior completed tool cycles.
    /// Removed descendants lose their change destination before their resources are released.
    /// </summary>
    public void TruncateSpans(int retainedCount)
    {
        if (_spans.Count <= retainedCount)
        {
            return;
        }

        var removed = _spans.Skip(retainedCount).ToArray();
        Volatile.Write(ref _spans, _spans.AsValueEnumerable().Take(retainedCount).ToArray());
        foreach (var span in removed)
        {
            span.Owner = null;
            if (span is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        NotifyChanged();
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var span in Spans
                     .AsValueEnumerable()
                     .OfType<AssistantChatMessageTextSpan>()
                     .Where(s => s.ContentMarkdownBuilder.Length > 0))
        {
            builder.AppendLine(span.ContentMarkdownBuilder.ToString());
        }

        return builder.TrimEnd().ToString();
    }

    /// <inheritdoc />
    protected override void OnOwnerChanged(ChatMessageNode? owner)
    {
        UsageDetails.Owner = owner;

        foreach (var span in _spans)
        {
            span.Owner = owner;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        foreach (var span in _spans)
        {
            span.Owner = null;
            if (span is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}