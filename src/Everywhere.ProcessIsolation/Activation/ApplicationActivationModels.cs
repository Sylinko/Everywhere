using MessagePack;

namespace Everywhere.ProcessIsolation.Activation;

/// <summary>External activation data; validated before queue acceptance.</summary>
[Union(0, typeof(ShowChatWindowActivationRequest))]
[Union(1, typeof(UrlCallbackActivationRequest))]
public abstract class ApplicationActivationRequest
{
    /// <summary>
    /// Validates untrusted deserialized data, including null URLs despite the non-null construction contract.
    /// </summary>
    [IgnoreMember]
    public virtual bool IsValid => false;
}

/// <summary>Shows the chat window without an arbitrary navigation route.</summary>
[MessagePackObject]
public sealed partial class ShowChatWindowActivationRequest : ApplicationActivationRequest
{
    [IgnoreMember]
    public override bool IsValid => true;
}

/// <summary>Delivers a protocol callback to Main's current application state.</summary>
/// <param name="url">The callback URL, validated before queue acceptance.</param>
[MessagePackObject]
public sealed partial class UrlCallbackActivationRequest(string url) : ApplicationActivationRequest
{
    /// <summary>
    /// The application's registered URL scheme.
    /// </summary>
    public const string UrlScheme = "sylinko-everywhere";

    /// <summary>
    /// Maximum callback URL length, in UTF-16 code units.
    /// </summary>
    public const int MaximumUrlLength = 16 * 1024;

    /// <summary>
    /// The absolute callback URL using the registered application scheme.
    /// </summary>
    [Key(0)]
    public string Url { get; } = url;

    [IgnoreMember]
    public override bool IsValid => Url is { Length: > 0 and <= MaximumUrlLength } &&
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, UrlScheme, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Main's disposition of an activation request.</summary>
public enum ApplicationActivationStatus
{
    /// <summary>Main owns the queued request, regardless of the client's subsequent lifetime.</summary>
    Accepted,
    /// <summary>The external command shape or URL is invalid.</summary>
    InvalidRequest,
    /// <summary>The bounded startup or delivery queue is full.</summary>
    Busy,
    /// <summary>Main is no longer accepting requests.</summary>
    ShuttingDown
}

/// <summary>Explicit activation acceptance response.</summary>
[MessagePackObject]
public sealed partial class ApplicationActivationResponse
{
    /// <summary>Whether Main accepted ownership or why it rejected the request.</summary>
    [Key(0)]
    public ApplicationActivationStatus Status { get; init; }
}