using System.Text;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicData;
using Everywhere.Collections;
using Everywhere.Common;
using MessagePack;
using Microsoft.SemanticKernel.ChatCompletion;
using ZLinq;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class AssistantChatMessage : ChatMessage, IHaveChatAttachments, ISourceList<AssistantChatMessageSpan>
{
    public override AuthorRole Role => AuthorRole.Assistant;

    [Key(0)]
    private string? Content
    {
        get => null; // for forward compatibility
        init
        {
            if (!value.IsNullOrEmpty())
            {
                _spansSource.Edit(list => list.Add(new AssistantChatMessageTextSpan(value)));
            }
        }
    }

    [Key(1)]
    [ObservableProperty]
    public partial IDynamicLocaleKey? ErrorMessageKey { get; set; }

    [Key(2)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedSeconds))]
    public partial DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Key(3)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedSeconds))]
    public partial DateTimeOffset FinishedAt { get; set; }

    [IgnoreMember]
    [JsonIgnore]
    public double ElapsedSeconds => Math.Max((FinishedAt - CreatedAt).TotalSeconds, 0);

    [Key(4)]
    private IList<FunctionCallChatMessage>? FunctionCalls
    {
        get => null; // for forward compatibility
        init
        {
            if (value is { Count: > 0 })
            {
                _spansSource.Edit(list => list.Add(new AssistantChatMessageFunctionCallSpan(value)));
            }
        }
    }

    [Key(5)]
    [Obsolete]
    private IEnumerable<LegacyAssistantChatMessageSpan>? LegacySerializableSpans
    {
        get => null; // For forward compatibility
        init
        {
            if (value is null) return;
            _spansSource.Edit(list =>
            {
                list.Clear();
                foreach (var legacySpan in value)
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

    /// <summary>
    /// Each span represents a part of the message content and function calls.
    /// </summary>
    [IgnoreMember]
    public IReadOnlyBindableList<AssistantChatMessageSpan> Spans { get; }

    [Key(9)]
    [ObservableProperty]
    public partial MetadataDictionary? Metadata { get; set; }

    [Key(10)]
    private IEnumerable<AssistantChatMessageSpan>? SerializableSpans
    {
        get => _spansSource.Items;
        set
        {
            if (value is null) return;
            _spansSource.Edit(list => list.Reset(value));
        }
    }

    [Key(11)]
    [ObservableProperty]
    public partial ChatUsageDetails UsageDetails { get; private set; } = new();

    [IgnoreMember]
    public IEnumerable<ChatAttachment> Attachments => _spansSource.Items.OfType<IHaveChatAttachments>().SelectMany(s => s.Attachments);

    /// <summary>
    /// The private source for function calls.
    /// </summary>
    [IgnoreMember] private readonly SourceList<AssistantChatMessageSpan> _spansSource = new();
    [IgnoreMember] private readonly IDisposable _spansConnection;

    public AssistantChatMessage()
    {
        Spans = _spansSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .DisposeMany()
            .BindEx(out _spansConnection);
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