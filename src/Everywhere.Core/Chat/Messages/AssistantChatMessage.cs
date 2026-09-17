using System.Text;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Collections;
using Everywhere.Common;
using MessagePack;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class AssistantChatMessage : ChatMessage, IHaveChatAttachments, ISourceList<AssistantChatMessageSpan>
{
    public override AuthorRole Role => AuthorRole.Assistant;

    [Key(0)]
#pragma warning disable CA1822
    // ReSharper disable once MemberCanBeMadeStatic.Local
    // for forward compatibility
    private string? LegacyContent => null;
#pragma warning restore CA1822

    [Key(1)]
    [ObservableProperty]
    public partial IDynamicLocaleKey? ErrorMessageKey { get; set; }

    [Key(2)]
    public override DateTimeOffset CreatedAt { get; }

    [Key(3)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedSeconds))]
    public partial DateTimeOffset FinishedAt { get; set; }

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
    public IReadOnlyBindableList<AssistantChatMessageSpan> Spans { get; }

    [Key(9)]
    [ObservableProperty]
    public partial MetadataDictionary Metadata { get; set; }

    [Key(10)]
    private IEnumerable<AssistantChatMessageSpan> SerializableSpans => _spansSource.Items;

    [Key(11)]
    public ChatUsageDetails UsageDetails { get; }

    [IgnoreMember]
    public IEnumerable<ChatAttachment> Attachments => _spansSource.Items.OfType<IHaveChatAttachments>().SelectMany(s => s.Attachments);

    /// <summary>
    /// The private source for function calls.
    /// </summary>
    [IgnoreMember] private readonly SourceList<AssistantChatMessageSpan> _spansSource = new();
    [IgnoreMember] private readonly IDisposable _spansConnection;
    [IgnoreMember] private readonly IDisposable _spansPersistenceConnection;

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
        ChatUsageDetails? usageDetails)
    {
        if (!obsoletedContent.IsNullOrEmpty())
        {
            _spansSource.Edit(list => list.Add(new AssistantChatMessageTextSpan(obsoletedContent)));
        }

        ErrorMessageKey = errorMessageKey;
        CreatedAt = createdAt;
        FinishedAt = finishedAt;
        Metadata = metadata;
        UsageDetails = usageDetails ?? new ChatUsageDetails();

        if (serializableSpans is not null)
        {
            _spansSource.Edit(list => list.Reset(serializableSpans));
        }
        else
        {
            if (legacyFunctionCalls is { Count: > 0 })
            {
                _spansSource.Edit(list => list.Add(new AssistantChatMessageFunctionCallSpan(legacyFunctionCalls)));
            }
            if (legacySerializableSpans is not null)
            {
                _spansSource.Edit(list =>
                {
                    list.Clear();
                    foreach (var legacySpan in legacySerializableSpans)
                    {
                        if (legacySpan.ReasoningOutput is { Length: > 0 } reasoningOutput)
                        {
                            list.Add(
                                new AssistantChatMessageReasoningSpan(reasoningOutput)
                                {
                                    CreatedAt = legacySpan.CreatedAt,
                                    FinishedAt = legacySpan.ReasoningFinishedAt ?? legacySpan.FinishedAt
                                });
                        }

                        if (legacySpan.FunctionCalls is { Count: > 0 } functionCalls)
                        {
                            list.Add(
                                new AssistantChatMessageFunctionCallSpan(functionCalls)
                                {
                                    CreatedAt = legacySpan.CreatedAt,
                                    FinishedAt = legacySpan.FinishedAt
                                });
                        }

                        if (legacySpan.Content is { Length: > 0 } content)
                        {
                            list.Add(
                                new AssistantChatMessageTextSpan(content)
                                {
                                    CreatedAt = legacySpan.CreatedAt,
                                    FinishedAt = legacySpan.FinishedAt
                                });
                        }
                    }
                });
            }
        }

        Spans = _spansSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .DisposeMany()
            .BindEx(out _spansConnection);

        // Keep persistence changes separate from the UI binding pipeline. AutoRefresh
        // subscribes to each span while it belongs to the source and releases that
        // subscription when the span is removed, so a removed span cannot retain this
        // message through an event handler.
        _spansPersistenceConnection = _spansSource
            .Connect()
            .AutoRefresh()
            .Subscribe(_ => OnPropertyChanged(nameof(Spans)));
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
        usageDetails: null)
    {
    }

    public void AddSpan(AssistantChatMessageSpan span)
    {
        _spansSource.Add(span);
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var span in _spansSource.Items
                     .AsValueEnumerable()
                     .OfType<AssistantChatMessageTextSpan>()
                     .Where(s => s.ContentMarkdownBuilder.Length > 0))
        {
            builder.AppendLine(span.ContentMarkdownBuilder.ToString());
        }

        return builder.TrimEnd().ToString();
    }

    public void Dispose()
    {
        _spansPersistenceConnection.Dispose();

        _spansSource.Edit(list =>
        {
            foreach (var disposable in list.AsValueEnumerable().OfType<IDisposable>())
            {
                disposable.Dispose();
            }
        });

        _spansSource.Dispose();
        _spansConnection.Dispose();
    }

    #region ISourceList<AssistantChatMessageSpan> Implementation

    [IgnoreMember]
    public int Count => _spansSource.Count;

    [IgnoreMember]
    public IObservable<int> CountChanged => _spansSource.CountChanged;

    [IgnoreMember]
    public IReadOnlyList<AssistantChatMessageSpan> Items => _spansSource.Items;

    public IObservable<IChangeSet<AssistantChatMessageSpan>> Connect(Func<AssistantChatMessageSpan, bool>? predicate = null)
    {
        return _spansSource.Connect(predicate);
    }

    public IObservable<IChangeSet<AssistantChatMessageSpan>> Preview(Func<AssistantChatMessageSpan, bool>? predicate = null)
    {
        return _spansSource.Preview(predicate);
    }

    public void Edit(Action<IExtendedList<AssistantChatMessageSpan>> updateAction)
    {
        _spansSource.Edit(updateAction);
    }

    #endregion

}