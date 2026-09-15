using Everywhere.Automation;
using Everywhere.ProcessIsolation.Rpc;
using MessagePack;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Base contract for strongly typed failures returned by the Automation Host.</summary>
[MessagePackObject]
[Union(0, typeof(VisualElementQueryRpcError))]
public abstract partial class AutomationRpcError;

/// <summary>Describes a failed visual-element provider operation without exposing platform error details.</summary>
[MessagePackObject]
public sealed partial class VisualElementQueryRpcError : AutomationRpcError
{
    /// <summary>Platform-independent failure classification.</summary>
    [Key(0)]
    public required VisualElementQueryFailureKind Kind { get; init; }
}

/// <summary>Maps visual-element provider exceptions between Automation Host and Main.</summary>
public static class AutomationRpcExceptionMapping
{
    /// <summary>Gets the visual-query classification represented by an exception.</summary>
    public static bool TryGetVisualElementQueryFailureKind(
        Exception exception,
        out VisualElementQueryFailureKind failureKind)
    {
        var mappedKind = exception switch
        {
            UnauthorizedAccessException => (VisualElementQueryFailureKind?)VisualElementQueryFailureKind.PermissionDenied,
            TimeoutException => VisualElementQueryFailureKind.Timeout,
            NotSupportedException => VisualElementQueryFailureKind.Unsupported,
            VisualElementProviderException providerException => providerException.Kind,
            _ => null,
        };
        failureKind = mappedKind.GetValueOrDefault();
        return mappedKind.HasValue;
    }

    internal static Exception CreateException(VisualElementQueryRpcError error) => error.Kind switch
    {
        VisualElementQueryFailureKind.PermissionDenied => new UnauthorizedAccessException("The Automation Host denied access to the visual element."),
        VisualElementQueryFailureKind.Timeout => new TimeoutException("The Automation Host visual-element operation timed out."),
        VisualElementQueryFailureKind.Unsupported => new NotSupportedException("The Automation Host does not support this visual-element operation."),
        VisualElementQueryFailureKind.ElementUnavailable or VisualElementQueryFailureKind.ProviderFailure =>
            new VisualElementProviderException(error.Kind, "The Automation Host could not complete the visual-element operation."),
        _ => new InvalidOperationException($"The Automation Host returned an unsupported visual-query failure kind: {error.Kind}."),
    };
}

internal sealed class AutomationRpcExceptionMapper : IRpcExceptionMapper
{
    public static AutomationRpcExceptionMapper Shared { get; } = new();

    public bool TrySerialize(Exception exception, MessagePackRpcPayloadCodec payloadCodec, out byte[] payload)
    {
        if (!AutomationRpcExceptionMapping.TryGetVisualElementQueryFailureKind(exception, out var failureKind))
        {
            payload = [];
            return false;
        }

        payload = payloadCodec.Serialize<AutomationRpcError>(
            new VisualElementQueryRpcError
            {
                Kind = failureKind,
            });
        return true;
    }

    public Exception Deserialize(ReadOnlyMemory<byte> payload, MessagePackRpcPayloadCodec payloadCodec) =>
        payloadCodec.Deserialize<AutomationRpcError>(payload) switch
        {
            VisualElementQueryRpcError error => AutomationRpcExceptionMapping.CreateException(error),
            var error => throw new InvalidOperationException($"Unsupported Automation RPC error contract: {error.GetType().FullName}."),
        };
}