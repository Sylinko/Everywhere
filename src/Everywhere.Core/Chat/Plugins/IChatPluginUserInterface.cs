using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Everywhere.Chat.Permissions;
using Everywhere.Collections;
using Lucide.Avalonia;

namespace Everywhere.Chat.Plugins;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChatPluginTodoStatus
{
    NotStarted,
    InProgress,
    Completed
}

[Serializable]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public sealed record ChatPluginTodoItem
{
    [Description("ID of the todo item. Reset IDs must be unique; update IDs must already exist.")]
    public required int Id { get; init; }

    [MaxLength(300)]
    [Description("Todo title. Required for reset; omit during update to keep the current title.")]
    public string? Title { get; init; }

    [MaxLength(300)]
    [Description("Todo description. Omit during update to keep it; use an empty string to clear it.")]
    public string? Description { get; init; }

    [Description("Todo status. Omit during reset for NotStarted; omit during update to keep the current status.")]
    public ChatPluginTodoStatus? Status { get; init; }
}

public interface IChatPluginTodoItemsList : IReadOnlyBindableList<ChatPluginTodoItem>
{
    int CompletedCount { get; }

    ISourceList<ChatPluginTodoItem> SourceList { get; }
}

[Serializable]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public sealed class ChatPluginQuestion
{
    [MaxLength(75)]
    [Description("Short identifier for the question. Must be unique so answers can be mapped back to the question")]
    public required string Id { get; set; }

    [MaxLength(300)]
    [Description("The question text to display to the user. Keep it concise, ideally one sentence")]
    public required string Question { get; set; }

    [Description("Allow selecting multiple options when options are provided'")]
    public bool MultiSelect { get; set; }

    [Description("Optional list of selectable answers. If omitted, the question is free text")]
    public IReadOnlyList<ChatPluginQuestionOption>? Options { get; set; }
}

[Serializable]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public sealed class ChatPluginQuestionOption
{
    [Description("Main content for the option")]
    public required string Content { get; set; }

    [Description("Optional additional description for the option, such as implications of selecting it")]
    public string? Description { get; set; }

    [Description("Optional flag to indicate the option is recommended.")]
    public bool Recommended { get; set; }
}

[Serializable]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public sealed record ChatPluginQuestionAnswer(
    IReadOnlyList<string> Selected,
    string? FreeText
);

[Flags]
public enum RequestConsentRememberMasks
{
    AllowOnce = 0x1,
    AllowSession = 0x2,
    AlwaysAllow = 0x4,

    All = AllowOnce | AllowSession | AlwaysAllow
}

public sealed record RequestConsentCustomOption(object Key, IDynamicLocaleKey HeaderKey, LucideIconKind? Icon);

/// <summary>
/// Represents the effective result of a consent request returned to a chat plugin.
/// </summary>
/// <remarks>
/// Unlike <see cref="ConsentDecision"/>, this value is produced after the invocation context has
/// applied remembered approval state and converted the user's raw decision into an accepted or
/// denied outcome. Chat plugins generally only need <see cref="IsAccepted"/> and an optional
/// <see cref="CustomOption"/>; they do not need to interpret the original remember policy.
/// </remarks>
public readonly record struct RequestConsentResult(bool IsAccepted, string? Reason, RequestConsentCustomOption? CustomOption = null)
{
    public static RequestConsentResult Accept => new(true, null);

    public static RequestConsentResult Deny(string? reason = null) => new(false, reason);

    public static RequestConsentResult Custom(RequestConsentCustomOption customOption) => new(true, null, customOption);

    public static implicit operator bool(RequestConsentResult result) => result.IsAccepted;

    public string FormatReason(string prefix)
    {
        return Reason.IsNullOrWhiteSpace() ? prefix : $"{prefix} Reason: {Reason}";
    }
}

/// <summary>
/// Allows chat plugins to interact with the user interface.
/// </summary>
public interface IChatPluginUserInterface
{
    /// <summary>
    /// Gets a display sink for the plugin to output content to the user interface.
    /// </summary>
    /// <returns></returns>
    IChatPluginDisplaySink DisplaySink { get; }

    /// <summary>
    /// Gets or sets the lightweight, runtime-only preview for the current tool invocation.
    /// </summary>
    /// <remarks>
    /// This property belongs to the invocation context rather than <see cref="DisplaySink"/>:
    /// detailed display blocks are durable message content, while a preview is visible only while
    /// its invocation is alive. Assigning <see langword="null"/> hides the preview early; otherwise
    /// the invocation scope removes it automatically when the tool call completes.
    /// </remarks>
    ChatPluginActivityPreview? ActivityPreview { get; set; }

    /// <summary>
    /// Requests user consent for a permission request.
    /// </summary>
    /// <remarks>
    /// Consent is grouped by plugin.function.id, so multiple calls with the same parameters will only prompt the user once (if they choose to remember their decision).
    /// </remarks>
    /// <param name="id"></param>
    /// <param name="headerKey"></param>
    /// <param name="content"></param>
    /// <param name="rememberMasks"></param>
    /// <param name="customOptions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<RequestConsentResult> RequestConsentAsync(
        string? id,
        IDynamicLocaleKey headerKey,
        ChatPluginDisplayBlock? content = null,
        RequestConsentRememberMasks rememberMasks = RequestConsentRememberMasks.All,
        IReadOnlyList<RequestConsentCustomOption>? customOptions = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ask question and wait for answer.
    /// </summary>
    /// <param name="questions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IReadOnlyList<ChatPluginQuestionAnswer>> AskQuestionAsync(
        IReadOnlyList<ChatPluginQuestion> questions,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A broker interface to provide chat plugin user interface related services, such as displaying content and requesting user input.
/// </summary>
public interface IChatPluginUserInterfaceBroker
{
    /// <summary>
    /// Gets a list of user interface items to be displayed in the UI. The plugin can update this list to add/remove/modify items, and the UI will reactively update accordingly.
    /// </summary>
    IReadOnlyBindableList<ChatPluginUserInterfaceItem> ChatPluginUserInterfaceItems { get; }

    /// <summary>
    /// Gets a list of todo items to be displayed in the UI. The plugin can update this list to add/remove/modify todo items, and the UI will reactively update accordingly.
    /// </summary>
    IChatPluginTodoItemsList TodoItems { get; }

    /// <summary>
    /// Shows a consent request dialog to the user and returns their decision.
    /// </summary>
    /// <param name="headerKey"></param>
    /// <param name="content"></param>
    /// <param name="rememberMasks"></param>
    /// <param name="customOptions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<ConsentDecision> HandleConsentRequestAsync(
        IDynamicLocaleKey headerKey,
        ChatPluginDisplayBlock? content,
        RequestConsentRememberMasks rememberMasks,
        IReadOnlyList<RequestConsentCustomOption>? customOptions,
        CancellationToken cancellationToken);

    /// <summary>
    /// Shows a question dialog to the user and returns their answer.
    /// </summary>
    /// <param name="questions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IReadOnlyList<ChatPluginQuestionAnswer>> HandleAskQuestionAsync(
        IReadOnlyList<ChatPluginQuestion> questions,
        CancellationToken cancellationToken = default);
}
