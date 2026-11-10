using System.ComponentModel;
using System.Net.Sockets;
using System.Security;
using System.Text.Json;
using Everywhere.Cloud;
using MessagePack;
using Microsoft.SemanticKernel;

namespace Everywhere.Common;

/// <summary>
/// Represents errors that occur during application operations.
/// This is the base class for all custom exceptions in the application.
/// </summary>
public class HandledException : Exception
{
    /// <summary>
    /// Gets the key for a localized, user-friendly error message.
    /// </summary>
    public virtual IDynamicLocaleKey FriendlyMessageKey { get; }

    /// <summary>
    /// Gets a value indicating whether the error is a general, non-technical error that can be shown to the user.
    /// </summary>
    public virtual bool IsExpected { get; }

    public override string Message
    {
        get
        {
            var exception = InnerException;
            var message = exception?.Message;
            while (message.IsNullOrEmpty() && exception?.InnerException is not null)
            {
                exception = exception.InnerException;
                message = exception?.Message;
            }

            return message ?? LocaleKey.Common_Unknown.I18N();
        }
    }

    public HandledException(Exception originalException, string friendlyMessageKey, bool isExpected = true, bool showDetails = false) : this(
        originalException,
        new DynamicLocaleKey(friendlyMessageKey),
        isExpected,
        showDetails)
    {
    }

    public HandledException(
        Exception originalException,
        IDynamicLocaleKey friendlyMessageKey,
        bool isExpected = true,
        bool showDetails = false
    ) : base(null, originalException)
    {
        IsExpected = isExpected;
        FriendlyMessageKey = showDetails ?
            new AggregateDynamicLocaleKey(
                [
                    friendlyMessageKey,
                    new DirectLocaleKey(originalException.Message.Trim())
                ],
                "\n") :
            friendlyMessageKey;
    }

    protected HandledException(Exception originalException) : base(originalException.Message, originalException)
    {
        FriendlyMessageKey = CreateDefaultFriendlyMessage(originalException);
    }

    /// <summary>
    /// Creates fallback text for an unclassified exception, combining any type-specific
    /// localized description with the original message details.
    /// </summary>
    public static IDynamicLocaleKey CreateDefaultFriendlyMessage(Exception exception)
    {
        var exceptionName = exception.GetType().Name;
        if (exceptionName.EndsWith("Exception"))
        {
            exceptionName = exceptionName[..^"Exception".Length];
        }

        var details = new DirectLocaleKey(exception.Message.Trim());
        var messageKey = $"FriendlyExceptionMessage_{exceptionName}";
        return DynamicLocaleKey.Exists(messageKey) ?
            new AggregateDynamicLocaleKey([new DynamicLocaleKey(messageKey), details], "\n") :
            details;
    }
}

/// <summary>
/// Defines the types of system-level errors that can occur during general application operations,
/// such as filesystem, OS interop, network sockets, and cancellation/timeouts.
/// </summary>
public enum HandledSystemExceptionType
{
    /// <summary>
    /// An unknown or uncategorized system error.
    /// </summary>
    Unknown,

    /// <summary>
    /// The specified file could not be found.
    /// </summary>
    FileNotFound,

    /// <summary>
    /// The specified directory could not be found.
    /// </summary>
    DirectoryNotFound,

    /// <summary>
    /// The specified drive could not be found.
    /// </summary>
    DriveNotFound,

    /// <summary>
    /// The specified path is too long for the platform or API.
    /// </summary>
    PathTooLong,

    /// <summary>
    /// The end of the stream is reached unexpectedly.
    /// </summary>
    EndOfStream,

    /// <summary>
    /// A general I/O error (e.g., read/write/stream failures).
    /// </summary>
    IOException,

    /// <summary>
    /// Access to a resource is denied (permissions, ACLs, etc.).
    /// </summary>
    UnauthorizedAccess,

    /// <summary>
    /// The user is not logged in, which may be required for certain operations that involve user-specific resources or permissions.
    /// </summary>
    UserNotLogin,

    /// <summary>
    /// The operation was canceled.
    /// </summary>
    OperationCanceled,

    /// <summary>
    /// The operation exceeded the allotted time.
    /// </summary>
    Timeout,

    /// <summary>
    /// The requested operation is not supported by the platform or API.
    /// </summary>
    NotSupported,

    /// <summary>
    /// A security-related error (CAS, sandboxing, or other security checks).
    /// </summary>
    Security,

    /// <summary>
    /// Insufficient memory to continue the execution of the program.
    /// </summary>
    OutOfMemory,

    /// <summary>
    /// The data is invalid or in an unexpected format.
    /// </summary>
    InvalidData,

    /// <summary>
    /// The operation is not valid due to the current state of the object.
    /// </summary>
    InvalidOperation,

    /// <summary>
    /// A null argument was passed to a method that does not accept it.
    /// </summary>
    ArgumentNull,

    /// <summary>
    /// An argument is outside the range of valid values.
    /// </summary>
    ArgumentOutOfRange,

    /// <summary>
    /// The argument provided to a method is not valid.
    /// </summary>
    InvalidArgument,

    /// <summary>
    /// The format of an argument is not valid.
    /// </summary>
    InvalidFormat,

    /// <summary>
    /// Serialization related exception.
    /// </summary>
    Serialization,

    /// <summary>
    /// A COM interop error (HRESULT-based).
    /// </summary>
    COMException,

    /// <summary>
    /// A Win32 error (NativeErrorCode-based).
    /// </summary>
    Win32Exception,

    /// <summary>
    /// A socket-related network error (e.g., connection refused, unreachable).
    /// </summary>
    Socket,

    /// <summary>
    /// The SSL connection could not be established.
    /// </summary>
    SSLConnectionError,

    /// <summary>
    /// The connection was refused by the server.
    /// </summary>
    ConnectionRefused,

    /// <summary>
    /// The host could not be found (DNS error).
    /// </summary>
    HostNotFound,

    /// <summary>
    /// An error occurred while invoking a function or method.
    /// </summary>
    FunctionInvoking,

    /// <summary>
    /// A transport failure without a more specific socket, TLS, or timeout cause.
    /// </summary>
    Network,

    /// <summary>
    /// The configured proxy refused to establish a tunnel to the destination service.
    /// </summary>
    ProxyTunnelRejected
}

/// <summary>
/// Represents system-level errors (I/O, interop, OS, cancellation) in a normalized form.
/// </summary>
public class HandledSystemException : HandledException
{
    /// <summary>
    /// Gets the categorized type of the system exception.
    /// </summary>
    public HandledSystemExceptionType ExceptionType { get; }

    /// <summary>
    /// Gets an optional platform-specific error code (e.g., HRESULT, Win32, or Socket error code).
    /// </summary>
    public int? ErrorCode { get; init; }

    /// <summary>
    /// Initializes a new instance, inferring a user-friendly message key from the provided type,
    /// unless a custom key is supplied.
    /// </summary>
    public HandledSystemException(
        Exception originalException,
        HandledSystemExceptionType type,
        IDynamicLocaleKey? customFriendlyMessageKey = null,
        bool isExpected = true
    ) : base(
        originalException,
        customFriendlyMessageKey ?? new DynamicLocaleKey(
            type switch
            {
                HandledSystemExceptionType.FileNotFound => LocaleKey.HandledSystemException_FileNotFound,
                HandledSystemExceptionType.DirectoryNotFound => LocaleKey.HandledSystemException_DirectoryNotFound,
                HandledSystemExceptionType.DriveNotFound => LocaleKey.HandledSystemException_DriveNotFound,
                HandledSystemExceptionType.PathTooLong => LocaleKey.HandledSystemException_PathTooLong,
                HandledSystemExceptionType.EndOfStream => LocaleKey.HandledSystemException_EndOfStream,
                HandledSystemExceptionType.IOException => LocaleKey.HandledSystemException_IOException,
                HandledSystemExceptionType.UnauthorizedAccess => LocaleKey.HandledSystemException_UnauthorizedAccess,
                HandledSystemExceptionType.UserNotLogin => LocaleKey.HandledSystemException_UserNotLogin,
                HandledSystemExceptionType.OperationCanceled => LocaleKey.HandledSystemException_OperationCancelled,
                HandledSystemExceptionType.Timeout => LocaleKey.HandledSystemException_Timeout,
                HandledSystemExceptionType.NotSupported => LocaleKey.HandledSystemException_NotSupported,
                HandledSystemExceptionType.Security => LocaleKey.HandledSystemException_Security,
                HandledSystemExceptionType.OutOfMemory => LocaleKey.HandledSystemException_OutOfMemory,
                HandledSystemExceptionType.InvalidData => LocaleKey.HandledSystemException_InvalidData,
                HandledSystemExceptionType.InvalidOperation => LocaleKey.HandledSystemException_InvalidOperation,
                HandledSystemExceptionType.ArgumentNull => LocaleKey.HandledSystemException_ArgumentNull,
                HandledSystemExceptionType.ArgumentOutOfRange => LocaleKey.HandledSystemException_ArgumentOutOfRange,
                HandledSystemExceptionType.InvalidArgument => LocaleKey.HandledSystemException_InvalidArgument,
                HandledSystemExceptionType.InvalidFormat => LocaleKey.HandledSystemException_InvalidFormat,
                HandledSystemExceptionType.Serialization => LocaleKey.HandledSystemException_Serialization,
                HandledSystemExceptionType.COMException => LocaleKey.HandledSystemException_COMException,
                HandledSystemExceptionType.Win32Exception => LocaleKey.HandledSystemException_Win32Exception,
                HandledSystemExceptionType.Socket => LocaleKey.HandledSystemException_Socket,
                HandledSystemExceptionType.SSLConnectionError => LocaleKey.HandledSystemException_SSLConnectionError,
                HandledSystemExceptionType.ConnectionRefused => LocaleKey.HandledSystemException_ConnectionRefused,
                HandledSystemExceptionType.HostNotFound => LocaleKey.HandledSystemException_HostNotFound,
                HandledSystemExceptionType.FunctionInvoking => LocaleKey.HandledSystemException_FunctionInvoking,
                HandledSystemExceptionType.Network => LocaleKey.Common_NetworkError,
                HandledSystemExceptionType.ProxyTunnelRejected => LocaleKey.Common_ProxyTunnelRejected,
                _ => LocaleKey.HandledSystemException_Unknown,
            }),
        isExpected)
    {
        ExceptionType = type;
    }

    /// <summary>
    /// Normalizes common system and HTTP failures while preserving already-handled exception identity.
    /// HTTP responses retain their status in HandledHttpException; transport failures retain their concrete cause.
    /// </summary>
    public static Exception Handle(Exception exception, bool? isExpectedOverride = null)
    {
        switch (exception)
        {
            case HandledException handledException:
                return handledException;
            case AggregateException aggregateException:
                return new AggregateException(aggregateException.Segregate().Select(e => Handle(e, isExpectedOverride)));
        }

        var context = new ExceptionParsingContext(exception);
        if (!TryParseGeneralException(ref context))
        {
            if (HandleNetworkException(exception, isExpectedOverride) is { } networkError)
            {
                return networkError;
            }
            switch (exception)
            {
                case COMException com:
                    context.ExceptionType = HandledSystemExceptionType.COMException;
                    context.ErrorCode = com.ErrorCode;
                    break;
                case Win32Exception win32:
                    context.ExceptionType = HandledSystemExceptionType.Win32Exception;
                    context.ErrorCode = win32.NativeErrorCode;
                    break;
            }
        }

        return new HandledSystemException(
            originalException: exception,
            type: context.ExceptionType ?? HandledSystemExceptionType.Unknown,
            isExpected: isExpectedOverride ?? context.ExceptionType is null or HandledSystemExceptionType.Unknown)
        {
            ErrorCode = context.ErrorCode
        };
    }

    private ref struct ExceptionParsingContext(Exception exception)
    {
        public Exception Exception { get; } = exception;
        public HandledSystemExceptionType? ExceptionType { get; set; }
        public int? ErrorCode { get; set; }
    }

    /// <summary>
    /// Parses common system exceptions and IO-related subclasses.
    /// </summary>
    private static bool TryParseGeneralException(ref ExceptionParsingContext context)
    {
        switch (context.Exception)
        {
            case FileNotFoundException:
                context.ExceptionType = HandledSystemExceptionType.FileNotFound;
                break;
            case DirectoryNotFoundException:
                context.ExceptionType = HandledSystemExceptionType.DirectoryNotFound;
                break;
            case DriveNotFoundException:
                context.ExceptionType = HandledSystemExceptionType.DriveNotFound;
                break;
            case PathTooLongException:
                context.ExceptionType = HandledSystemExceptionType.PathTooLong;
                break;
            case EndOfStreamException:
                context.ExceptionType = HandledSystemExceptionType.EndOfStream;
                break;
            case IOException io when io is not HttpIOException:
                context.ExceptionType = HandledSystemExceptionType.IOException;
                context.ErrorCode ??= io.HResult;
                break;
            case UnauthorizedAccessException:
                context.ExceptionType = HandledSystemExceptionType.UnauthorizedAccess;
                break;
            case UserNotLoginException:
                context.ExceptionType = HandledSystemExceptionType.UserNotLogin;
                break;
            case OperationCanceledException:
                context.ExceptionType = NetworkExceptionEvidence.Extract(context.Exception).HasTimeoutCause ?
                    HandledSystemExceptionType.Timeout :
                    HandledSystemExceptionType.OperationCanceled;
                break;
            case TimeoutException:
                context.ExceptionType = HandledSystemExceptionType.Timeout;
                break;
            case NotSupportedException:
                context.ExceptionType = HandledSystemExceptionType.NotSupported;
                break;
            case SecurityException:
                context.ExceptionType = HandledSystemExceptionType.Security;
                break;
            case OutOfMemoryException:
                context.ExceptionType = HandledSystemExceptionType.OutOfMemory;
                break;
            case InvalidDataException:
                context.ExceptionType = HandledSystemExceptionType.InvalidData;
                break;
            case InvalidOperationException:
                context.ExceptionType = HandledSystemExceptionType.InvalidOperation;
                break;
            case ArgumentNullException:
                context.ExceptionType = HandledSystemExceptionType.ArgumentNull;
                break;
            case ArgumentOutOfRangeException:
                context.ExceptionType = HandledSystemExceptionType.ArgumentOutOfRange;
                break;
            case ArgumentException:
                context.ExceptionType = HandledSystemExceptionType.InvalidArgument;
                break;
            case FormatException:
                context.ExceptionType = HandledSystemExceptionType.InvalidFormat;
                break;
            case JsonException:
            case MessagePackSerializationException:
                context.ExceptionType = HandledSystemExceptionType.Serialization;
                break;
            default:
                return false;
        }

        // Populate HResult for exceptions that expose it.
        context.ErrorCode ??= context.Exception.HResult;
        return true;
    }

    private static HandledException? HandleNetworkException(Exception exception, bool? isExpectedOverride)
    {
        var evidence = NetworkExceptionEvidence.Extract(exception);
        var type = evidence switch
        {
            { HasTlsError: true } => HandledSystemExceptionType.SSLConnectionError,
            { HasProxyTunnelError: true } => HandledSystemExceptionType.ProxyTunnelRejected,
            { SocketError: SocketError.ConnectionRefused } => HandledSystemExceptionType.ConnectionRefused,
            { SocketError: SocketError.HostNotFound or SocketError.TryAgain } => HandledSystemExceptionType.HostNotFound,
            { SocketError: not null } => HandledSystemExceptionType.Socket,
            { HasTimeoutCause: true } => HandledSystemExceptionType.Timeout,
            { TransportException: not null } => HandledSystemExceptionType.Network,
            _ => (HandledSystemExceptionType?)null
        };

        if (type is { } networkType)
        {
            return new HandledSystemException(exception, networkType, isExpected: isExpectedOverride ?? false)
            {
                ErrorCode = networkType is HandledSystemExceptionType.Socket or HandledSystemExceptionType.ConnectionRefused or
                    HandledSystemExceptionType.HostNotFound ? (int?)evidence.SocketError : null
            };
        }

        // A response rejection is not a socket failure. SDK response formatting follows
        // the same HTTP path, but service-specific error envelopes remain with their owner.
        if (evidence.StatusCode is { } status)
        {
            return new HandledHttpException(exception, status, isExpectedOverride ?? false);
        }
        if (exception is HttpOperationException operation)
        {
            return new HandledHttpException(exception, operation.StatusCode, isExpectedOverride ?? false);
        }
        return null;
    }

}
