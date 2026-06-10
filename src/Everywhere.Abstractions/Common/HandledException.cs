using System.ComponentModel;
using System.Net.Sockets;
using System.Security;
using System.Security.Authentication;
using System.Text.Json;
using Everywhere.Cloud;
using MessagePack;

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
    public virtual IDynamicResourceKey FriendlyMessageKey { get; }

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

            return message ?? LocaleResolver.Common_Unknown;
        }
    }

    public HandledException(
        Exception originalException,
        IDynamicResourceKey friendlyMessageKey,
        bool isExpected = true,
        bool showDetails = true
    ) : base(null, originalException)
    {
        IsExpected = isExpected;
        FriendlyMessageKey = showDetails ?
            new AggregateDynamicResourceKey(
                [
                    friendlyMessageKey,
                    new DirectResourceKey(originalException.Message.Trim())
                ],
                "\n") :
            friendlyMessageKey;
    }

    protected HandledException(Exception originalException) : base(originalException.Message, originalException)
    {
        FriendlyMessageKey = new DirectResourceKey(originalException.Message.Trim());
    }

    protected readonly struct NetworkExceptionAnalysis
    {
        public bool IsSslError { get; init; }
        public SocketError? SocketError { get; init; }
        public bool IsTimeout { get; init; }
    }

    protected static NetworkExceptionAnalysis AnalyzeNetworkException(Exception exception)
    {
        // 1. Check for SSL (AuthenticationException) in chain
        var current = exception;
        while (current is not null)
        {
            if (current is AuthenticationException)
            {
                return new NetworkExceptionAnalysis { IsSslError = true };
            }
            current = current.InnerException;
        }

        // 2. Check for SocketException (Deep search)
        current = exception;
        while (current is not null)
        {
            if (current is SocketException socketEx)
            {
                return new NetworkExceptionAnalysis { SocketError = socketEx.SocketErrorCode };
            }
            current = current.InnerException;
        }

        // 3. Check for Timeout
        if (exception is TimeoutException ||
            exception.InnerException is TimeoutException ||
            (exception is TaskCanceledException && exception.InnerException is TimeoutException) ||
            (exception is OperationCanceledException && exception.InnerException is TimeoutException))
        {
            return new NetworkExceptionAnalysis { IsTimeout = true };
        }

        return new NetworkExceptionAnalysis();
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
    FunctionInvoking
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
        IDynamicResourceKey? customFriendlyMessageKey = null,
        bool isExpected = true
    ) : base(
        originalException,
        customFriendlyMessageKey ?? new DynamicResourceKey(
            type switch
            {
                HandledSystemExceptionType.ArgumentNull => LocaleKey.HandledSystemException_ArgumentNull,
                HandledSystemExceptionType.ArgumentOutOfRange => LocaleKey.HandledSystemException_ArgumentOutOfRange,
                HandledSystemExceptionType.COMException => LocaleKey.HandledSystemException_COMException,
                HandledSystemExceptionType.ConnectionRefused => LocaleKey.HandledSystemException_ConnectionRefused,
                HandledSystemExceptionType.DirectoryNotFound => LocaleKey.HandledSystemException_DirectoryNotFound,
                HandledSystemExceptionType.DriveNotFound => LocaleKey.HandledSystemException_DriveNotFound,
                HandledSystemExceptionType.EndOfStream => LocaleKey.HandledSystemException_EndOfStream,
                HandledSystemExceptionType.FileNotFound => LocaleKey.HandledSystemException_FileNotFound,
                HandledSystemExceptionType.FunctionInvoking => LocaleKey.HandledSystemException_FunctionInvoking,
                HandledSystemExceptionType.HostNotFound => LocaleKey.HandledSystemException_HostNotFound,
                HandledSystemExceptionType.InvalidArgument => LocaleKey.HandledSystemException_InvalidArgument,
                HandledSystemExceptionType.InvalidData => LocaleKey.HandledSystemException_InvalidData,
                HandledSystemExceptionType.InvalidFormat => LocaleKey.HandledSystemException_InvalidFormat,
                HandledSystemExceptionType.InvalidOperation => LocaleKey.HandledSystemException_InvalidOperation,
                HandledSystemExceptionType.IOException => LocaleKey.HandledSystemException_IOException,
                HandledSystemExceptionType.NotSupported => LocaleKey.HandledSystemException_NotSupported,
                HandledSystemExceptionType.OperationCanceled => LocaleKey.HandledSystemException_OperationCancelled,
                HandledSystemExceptionType.OutOfMemory => LocaleKey.HandledSystemException_OutOfMemory,
                HandledSystemExceptionType.PathTooLong => LocaleKey.HandledSystemException_PathTooLong,
                HandledSystemExceptionType.Security => LocaleKey.HandledSystemException_Security,
                HandledSystemExceptionType.Serialization => LocaleKey.HandledSystemException_Serialization,
                HandledSystemExceptionType.Socket => LocaleKey.HandledSystemException_Socket,
                HandledSystemExceptionType.SSLConnectionError => LocaleKey.HandledSystemException_SSLConnectionError,
                HandledSystemExceptionType.Timeout => LocaleKey.HandledSystemException_Timeout,
                HandledSystemExceptionType.UnauthorizedAccess => LocaleKey.HandledSystemException_UnauthorizedAccess,
                HandledSystemExceptionType.UserNotLogin => LocaleKey.HandledSystemException_UserNotLogin,
                HandledSystemExceptionType.Win32Exception => LocaleKey.HandledSystemException_Win32Exception,
                _ => LocaleKey.HandledSystemException_Unknown,
            }),
        isExpected)
    {
        ExceptionType = type;
    }

    /// <summary>
    /// Parses a generic Exception into a <see cref="HandledSystemException"/> or <see cref="AggregateException"/>.
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
        new ParserChain<GeneralExceptionParser,
            ParserChain<SocketExceptionParser,
                ParserChain<HttpRequestExceptionParser,
                    ParserChain<ComExceptionParser,
                        Win32ExceptionParser>>>>().TryParse(ref context);

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

    private readonly struct ParserChain<T1, T2> : IExceptionParser
        where T1 : struct, IExceptionParser
        where T2 : struct, IExceptionParser
    {
        public bool TryParse(ref ExceptionParsingContext context)
        {
            return default(T1).TryParse(ref context) || default(T2).TryParse(ref context);
        }
    }

    private interface IExceptionParser
    {
        bool TryParse(ref ExceptionParsingContext context);
    }

    /// <summary>
    /// Parses common system exceptions and IO-related subclasses.
    /// </summary>
    private readonly struct GeneralExceptionParser : IExceptionParser
    {
        public bool TryParse(ref ExceptionParsingContext context)
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
                case IOException io:
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
                    context.ExceptionType = context.Exception.InnerException is TimeoutException ?
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
    }

    /// <summary>
    /// Parses HttpRequestException instances.
    /// </summary>
    private readonly struct HttpRequestExceptionParser : IExceptionParser
    {
        public bool TryParse(ref ExceptionParsingContext context)
        {
            if (context.Exception is not HttpRequestException)
            {
                return false;
            }

            var analysis = AnalyzeNetworkException(context.Exception);

            if (analysis.IsSslError)
            {
                context.ExceptionType = HandledSystemExceptionType.SSLConnectionError;
                return true;
            }

            if (analysis.SocketError.HasValue)
            {
                context.ErrorCode = (int)analysis.SocketError.Value;
                context.ExceptionType = analysis.SocketError.Value switch
                {
                    SocketError.ConnectionRefused => HandledSystemExceptionType.ConnectionRefused,
                    SocketError.HostNotFound or SocketError.TryAgain => HandledSystemExceptionType.HostNotFound,
                    _ => HandledSystemExceptionType.Socket
                };
                return true;
            }

            if (analysis.IsTimeout)
            {
                context.ExceptionType = HandledSystemExceptionType.Timeout;
                return true;
            }

            context.ExceptionType = HandledSystemExceptionType.Socket; // Fallback to general socket/network error
            return true;
        }
    }

    /// <summary>
    /// Parses SocketException instances.
    /// </summary>
    private readonly struct SocketExceptionParser : IExceptionParser
    {
        public bool TryParse(ref ExceptionParsingContext context)
        {
            if (context.Exception is not SocketException)
            {
                return false;
            }

            var analysis = AnalyzeNetworkException(context.Exception);

            if (analysis.SocketError.HasValue)
            {
                context.ErrorCode = (int)analysis.SocketError.Value;
                context.ExceptionType = analysis.SocketError.Value switch
                {
                    SocketError.ConnectionRefused => HandledSystemExceptionType.ConnectionRefused,
                    SocketError.HostNotFound or SocketError.TryAgain => HandledSystemExceptionType.HostNotFound,
                    _ => HandledSystemExceptionType.Socket
                };
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Parses COMException instances (HRESULT-based).
    /// </summary>
    private readonly struct ComExceptionParser : IExceptionParser
    {
        public bool TryParse(ref ExceptionParsingContext context)
        {
            if (context.Exception is not COMException com)
            {
                return false;
            }

            context.ExceptionType = HandledSystemExceptionType.COMException;
            context.ErrorCode = com.ErrorCode; // HRESULT
            return true;
        }
    }

    /// <summary>
    /// Parses Win32Exception instances (NativeErrorCode-based).
    /// </summary>
    private readonly struct Win32ExceptionParser : IExceptionParser
    {
        public bool TryParse(ref ExceptionParsingContext context)
        {
            if (context.Exception is not Win32Exception win32)
            {
                return false;
            }

            context.ExceptionType = HandledSystemExceptionType.Win32Exception;
            context.ErrorCode = win32.NativeErrorCode;
            return true;
        }
    }
}