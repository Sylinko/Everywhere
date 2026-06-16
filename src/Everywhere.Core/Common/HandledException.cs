using System.ClientModel;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic.Exceptions;
using Everywhere.AI;
using Everywhere.Cloud;
using MessagePack;
using Microsoft.SemanticKernel;
using OllamaSharp.Models.Exceptions;

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

            return message ?? LocaleResolver.Common_Unknown;
        }
    }

    public HandledException(
        Exception originalException,
        IDynamicLocaleKey friendlyMessageKey,
        bool isExpected = true,
        bool showDetails = true
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
        FriendlyMessageKey = new DirectLocaleKey(originalException.Message.Trim());
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

/// <summary>
/// Defines the types of errors that can occur during a request to an AI kernel or service.
/// </summary>
public enum HandledChatExceptionType
{
    /// <summary>
    /// An unknown error occurred.
    /// </summary>
    Unknown,

    /// <summary>
    /// Model is not configured correctly. The model provider or ID might be missing or incorrect.
    /// </summary>
    InvalidConfiguration,

    /// <summary>
    /// API key is missing or invalid.
    /// </summary>
    InvalidApiKey,

    /// <summary>
    /// The request exceeds the model's context length limit.
    /// </summary>
    ContextLengthExceeded,

    /// <summary>
    /// You have exceeded your API usage quota.
    /// </summary>
    QuotaExceeded,

    /// <summary>
    /// You have exceeded the request rate limit. Please try again later.
    /// </summary>
    RateLimit,

    /// <summary>
    /// Service endpoint is not reachable. Please check your network connection.
    /// </summary>
    EndpointNotReachable,

    /// <summary>
    /// Provided service endpoint is invalid.
    /// </summary>
    InvalidEndpoint,

    /// <summary>
    /// Service returned an empty response, which may indicate a network or service issue.
    /// </summary>
    EmptyResponse,

    /// <summary>
    /// Selected model does not support the requested feature.
    /// </summary>
    FeatureNotSupport,

    /// <summary>
    /// Thought signature is missing or invalid.
    /// </summary>
    InvalidThoughtSignature,

    /// <summary>
    /// The reasoning content provided is invalid or not supported by the selected model.
    /// </summary>
    InvalidReasoningContent,

    /// <summary>
    /// Selected model does not support image input.
    /// </summary>
    ImageNotSupport,

    /// <summary>
    /// Selected model does not support "temperature" customization or the provided value is out of range.
    /// </summary>
    TemperatureNotSupport,

    /// <summary>
    /// Selected model does not support "top_p" customization or the provided value is out of range.
    /// </summary>
    TopPNotSupport,

    /// <summary>
    /// Service does not support requests from your current region or location.
    /// </summary>
    RegionNotSupport,

    /// <summary>
    /// Request to the service timed out. Please try again.
    /// </summary>
    Timeout,

    /// <summary>
    /// A network error occurred. Please check your connection and try again.
    /// </summary>
    NetworkError,

    /// <summary>
    /// Service is currently unavailable. Please try again later.
    /// </summary>
    ServiceUnavailable,

    /// <summary>
    /// The user is not logged in, which may be required for certain operations that involve user-specific resources or permissions.
    /// </summary>
    UserNotLogin,

    /// <summary>
    /// Operation was canceled.
    /// </summary>
    OperationCanceled,

    /// <summary>
    /// An error occurred while parsing the response from the service, indicating an unexpected JSON format or content.
    /// </summary>
    JsonError,

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
}

/// <summary>
/// Represents errors that occur during requests to LLM providers.
/// This class normalizes various provider-specific exceptions into a unified format.
/// </summary>
public class HandledChatException(
    Exception originalException,
    HandledChatExceptionType type,
    DynamicLocaleKey? customFriendlyMessageKey = null,
    string? detailedMessage = null
) : HandledException(originalException)
{
    /// <summary>
    /// Gets a value indicating whether the error is a general, non-technical error.
    /// It is considered general unless the type is <see cref="HandledChatExceptionType.Unknown"/>.
    /// </summary>
    public override bool IsExpected => ExceptionType != HandledChatExceptionType.Unknown;

    public override IDynamicLocaleKey FriendlyMessageKey
    {
        get
        {
            if (field is not null) return field;

            var parts = new List<IDynamicLocaleKey>
            {
                customFriendlyMessageKey ?? new DynamicLocaleKey(
                    ExceptionType switch
                    {
                        HandledChatExceptionType.InvalidConfiguration => LocaleKey.HandledChatException_InvalidConfiguration,
                        HandledChatExceptionType.InvalidApiKey => LocaleKey.HandledChatException_InvalidApiKey,
                        HandledChatExceptionType.ContextLengthExceeded => LocaleKey.HandledChatException_ContextLengthExceeded,
                        HandledChatExceptionType.QuotaExceeded => LocaleKey.HandledChatException_QuotaExceeded,
                        HandledChatExceptionType.RateLimit => LocaleKey.HandledChatException_RateLimit,
                        HandledChatExceptionType.EndpointNotReachable => LocaleKey.HandledChatException_EndpointNotReachable,
                        HandledChatExceptionType.InvalidEndpoint => LocaleKey.HandledChatException_InvalidEndpoint,
                        HandledChatExceptionType.EmptyResponse => LocaleKey.HandledChatException_EmptyResponse,
                        HandledChatExceptionType.FeatureNotSupport => LocaleKey.HandledChatException_FeatureNotSupport,
                        HandledChatExceptionType.InvalidThoughtSignature => LocaleKey.HandledChatException_InvalidThoughtSignature,
                        HandledChatExceptionType.InvalidReasoningContent => LocaleKey.HandledChatException_InvalidReasoningContent,
                        HandledChatExceptionType.ImageNotSupport => LocaleKey.HandledChatException_ImageNotSupport,
                        HandledChatExceptionType.TemperatureNotSupport => LocaleKey.HandledChatException_TemperatureNotSupport,
                        HandledChatExceptionType.TopPNotSupport => LocaleKey.HandledChatException_TopPNotSupport,
                        HandledChatExceptionType.RegionNotSupport => LocaleKey.HandledChatException_RegionNotSupport,
                        HandledChatExceptionType.Timeout => LocaleKey.HandledChatException_Timeout,
                        HandledChatExceptionType.NetworkError => LocaleKey.HandledChatException_NetworkError,
                        HandledChatExceptionType.ServiceUnavailable => LocaleKey.HandledChatException_ServiceUnavailable,
                        HandledChatExceptionType.UserNotLogin => LocaleKey.HandledSystemException_UserNotLogin,
                        HandledChatExceptionType.OperationCanceled => LocaleKey.HandledChatException_OperationCanceled,
                        HandledChatExceptionType.SSLConnectionError => LocaleKey.HandledSystemException_SSLConnectionError,
                        HandledChatExceptionType.ConnectionRefused => LocaleKey.HandledSystemException_ConnectionRefused,
                        HandledChatExceptionType.HostNotFound => LocaleKey.HandledSystemException_HostNotFound,
                        _ => LocaleKey.HandledChatException_Unknown,
                    })
            };

            if (Message.Trim() is { Length: > 0 } trimmedMessage)
            {
                parts.Add(new DirectLocaleKey(trimmedMessage));
            }

            if (detailedMessage?.Trim() is { Length: > 0 } trimmedDetailedMessage)
            {
                parts.Add(new DirectLocaleKey(trimmedDetailedMessage));
            }

            return field = new AggregateDynamicLocaleKey(parts, "\n");
        }
    }

    /// <summary>
    /// Gets the categorized type of the exception.
    /// </summary>
    public HandledChatExceptionType ExceptionType { get; } = type;

    /// <summary>
    /// Gets the HTTP status code of the response, if available.
    /// </summary>
    public HttpStatusCode? StatusCode { get; init; }

    /// <summary>
    /// Gets the socket error code, if applicable.
    /// </summary>
    public SocketError? SocketError { get; init; }

    /// <summary>
    /// Gets the ID of the model associated with the request.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Parses a generic <see cref="Exception"/> into a <see cref="HandledChatException"/> or <see cref="AggregateException"/>.
    /// </summary>
    /// <param name="exception">The exception to parse.</param>
    /// <param name="kernelMixin"></param>
    /// <returns>A new instance of <see cref="HandledChatException"/>.</returns>
    public static Exception Handle(Exception exception, KernelMixin? kernelMixin)
    {
        if (kernelMixin is not null)
        {
            exception = kernelMixin.TransformChatException(exception);
        }

        switch (exception)
        {
            case HandledException handledException:
                return handledException;
            case AggregateException aggregateException:
                return new AggregateException(aggregateException.Segregate().Select(e => Handle(e, kernelMixin)));
        }

        var context = new ExceptionParsingContext(exception);

        // First layer: provider-specific exceptions
        new ParserChain<ClientResultExceptionParser,
            ParserChain<HttpOperationExceptionParser,
                ParserChain<AnthropicExceptionParser,
                    ParserChain<OllamaExceptionParser,
                        ParserChain<HttpRequestExceptionParser,
                            ParserChain<GeneralChatExceptionParser,
                                ParserChain<SocketExceptionParser,
                                    HttpStatusCodeParser>>>>>>>().TryParse(ref context);

        return new HandledChatException(
            originalException: exception,
            type: context.ExceptionType ?? HandledChatExceptionType.Unknown,
            detailedMessage: context.DetailedMessage)
        {
            StatusCode = context.StatusCode,
            SocketError = context.SocketError,
            ModelId = kernelMixin?.ModelId,
        };
    }

    public static HandledChatException FromErrorCode(Exception exception, string code)
    {
        return new HandledChatException(
            originalException: exception,
            type: IExceptionParser.ParseMessage(code) ?? HandledChatExceptionType.Unknown);
    }

    private ref struct ExceptionParsingContext(Exception exception)
    {
        public Exception Exception { get; } = exception;
        public HandledChatExceptionType? ExceptionType { get; set; }
        public HttpStatusCode? StatusCode { get; set; }
        public SocketError? SocketError { get; set; }
        public string? DetailedMessage { get; set; }
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

        /// <summary>
        /// Parses the exception message to identify specific error types when status codes are not sufficient.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="fallback"></param>
        /// <returns></returns>
        static HandledChatExceptionType? ParseMessage(string? message, HandledChatExceptionType? fallback = null)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return fallback;
            }

            if (message.Contains("signature", StringComparison.OrdinalIgnoreCase))
            {
                return HandledChatExceptionType.InvalidThoughtSignature;
            }

            if (message.Contains("reasoning_content", StringComparison.OrdinalIgnoreCase))
            {
                return HandledChatExceptionType.InvalidReasoningContent;
            }

            if (message.Contains("image_url", StringComparison.OrdinalIgnoreCase))
            {
                return HandledChatExceptionType.ImageNotSupport;
            }

            if (message.Contains("temperature", StringComparison.OrdinalIgnoreCase))
            {
                return HandledChatExceptionType.TemperatureNotSupport;
            }

            if (message.Contains("top_p", StringComparison.OrdinalIgnoreCase) || message.Contains("topP", StringComparison.OrdinalIgnoreCase))
            {
                return HandledChatExceptionType.TopPNotSupport;
            }

            if (message.Contains("region", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("location", StringComparison.OrdinalIgnoreCase))
            {
                return HandledChatExceptionType.RegionNotSupport;
            }

            if (message.Contains("context", StringComparison.OrdinalIgnoreCase) &&
                (message.Contains("length", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("exceeded", StringComparison.OrdinalIgnoreCase)))
            {
                return HandledChatExceptionType.ContextLengthExceeded;
            }

            if (message.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("exceeded", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("usage", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("organization", StringComparison.OrdinalIgnoreCase))
            {
                return HandledChatExceptionType.QuotaExceeded;
            }

            if (message.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("permission", StringComparison.OrdinalIgnoreCase))
            {
                return HandledChatExceptionType.InvalidApiKey;
            }

            if (message.Contains("model", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("parameter", StringComparison.OrdinalIgnoreCase))
            {
                return HandledChatExceptionType.InvalidConfiguration;
            }

            return fallback;
        }
    }

    private struct ClientResultExceptionParser : IExceptionParser
    {
        public bool TryParse(ref ExceptionParsingContext context)
        {
            if (context.Exception is not ClientResultException clientResult)
            {
                return false;
            }

            if (clientResult.Status == 0)
            {
                context.ExceptionType = HandledChatExceptionType.EmptyResponse;
                return true;
            }

            context.StatusCode = (HttpStatusCode)clientResult.Status;
            context.ExceptionType = IExceptionParser.ParseMessage(clientResult.Message);
            return false;
        }
    }

    private readonly struct HttpRequestExceptionParser : IExceptionParser
    {
        public bool TryParse(ref ExceptionParsingContext context)
        {
            if (context.Exception is not HttpRequestException httpRequest)
            {
                return false;
            }

            if (httpRequest.StatusCode.HasValue)
            {
                context.StatusCode = httpRequest.StatusCode.Value;
            }

            var analysis = AnalyzeNetworkException(context.Exception);

            if (analysis.IsSslError)
            {
                context.ExceptionType = HandledChatExceptionType.SSLConnectionError;
                return true;
            }

            if (analysis.SocketError.HasValue)
            {
                context.SocketError = analysis.SocketError.Value;
                context.ExceptionType = analysis.SocketError.Value switch
                {
                    System.Net.Sockets.SocketError.ConnectionRefused => HandledChatExceptionType.ConnectionRefused,
                    System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.TryAgain => HandledChatExceptionType.HostNotFound,
                    _ => HandledChatExceptionType.NetworkError
                };
                return true;
            }

            if (analysis.IsTimeout)
            {
                context.ExceptionType = HandledChatExceptionType.Timeout;
                return true;
            }

            if (httpRequest.StatusCode.HasValue)
            {
                // If we have a status code but no specific network error, let the chain continue to HttpStatusCodeParser
                // But wait, if we return false, the chain continues.
                // If we return true, the chain stops.
                // We want to stop if we found a specific error.
                // If we only found a status code, we want to let HttpStatusCodeParser handle it?
                // Actually, HttpStatusCodeParser is in the second chain.
                // If we return false here, the first chain continues to OllamaExceptionParser etc.
                // If the first chain finishes with false, the second chain runs.
                // So returning false is correct if we want HttpStatusCodeParser to run.
                return false;
            }

            context.ExceptionType = HandledChatExceptionType.EndpointNotReachable;
            return true;
        }
    }

    private readonly struct AnthropicExceptionParser : IExceptionParser
    {
        public bool TryParse(ref ExceptionParsingContext context)
        {
            if (context.Exception is not AnthropicException anthropicException)
            {
                return false;
            }

            context.ExceptionType = anthropicException switch
            {
                AnthropicRateLimitException => HandledChatExceptionType.RateLimit,
                AnthropicUnauthorizedException => HandledChatExceptionType.InvalidConfiguration,
                Anthropic5xxException => HandledChatExceptionType.ServiceUnavailable,
                AnthropicForbiddenException forbidden => IExceptionParser.ParseMessage(
                    forbidden.ResponseBody,
                    HandledChatExceptionType.RegionNotSupport),
                AnthropicApiException api => IExceptionParser.ParseMessage(api.ResponseBody, HandledChatExceptionType.InvalidConfiguration),
                _ => HandledChatExceptionType.Unknown
            };

            if (anthropicException is AnthropicApiException apiException)
            {
                context.DetailedMessage = apiException.ResponseBody;
                context.StatusCode = apiException.StatusCode;
            }

            return true;
        }
    }

    private readonly struct OllamaExceptionParser : IExceptionParser
    {
        public bool TryParse(ref ExceptionParsingContext context)
        {
            if (context.Exception is not OllamaException ollama)
            {
                return false;
            }

            var message = ollama.Message;
            if (message.Contains("model", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                context.ExceptionType = HandledChatExceptionType.InvalidConfiguration;
            }
            else
            {
                context.ExceptionType = HandledChatExceptionType.Unknown;
            }
            return true;
        }
    }

    private readonly struct HttpOperationExceptionParser : IExceptionParser
    {
        public bool TryParse(ref ExceptionParsingContext context)
        {
            if (context.Exception is not HttpOperationException httpOperation)
            {
                return false;
            }

            context.StatusCode = httpOperation.StatusCode;
            context.DetailedMessage = httpOperation.ResponseContent;
            context.ExceptionType = IExceptionParser.ParseMessage(httpOperation.ResponseContent);
            return true;
        }
    }

    private readonly struct SocketExceptionParser : IExceptionParser
    {
        public bool TryParse(ref ExceptionParsingContext context)
        {
            var analysis = AnalyzeNetworkException(context.Exception);
            if (!analysis.SocketError.HasValue) return false;

            context.SocketError = analysis.SocketError.Value;
            context.ExceptionType = analysis.SocketError.Value switch
            {
                System.Net.Sockets.SocketError.ConnectionRefused => HandledChatExceptionType.ConnectionRefused,
                System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.TryAgain => HandledChatExceptionType.HostNotFound,
                _ => HandledChatExceptionType.NetworkError
            };
            return true;
        }
    }

    private readonly struct GeneralChatExceptionParser : IExceptionParser
    {
        public bool TryParse(ref ExceptionParsingContext context)
        {
            context.ExceptionType = context.Exception switch
            {
                ModelDoesNotSupportToolsException => HandledChatExceptionType.FeatureNotSupport,
                AuthenticationException => HandledChatExceptionType.InvalidApiKey,
                UriFormatException => HandledChatExceptionType.InvalidEndpoint,
                UserNotLoginException => HandledChatExceptionType.UserNotLogin,
                OperationCanceledException => context.Exception.InnerException is TimeoutException ?
                    HandledChatExceptionType.Timeout :
                    HandledChatExceptionType.OperationCanceled,
                JsonException => HandledChatExceptionType.JsonError,
                _ => null
            };
            return context.ExceptionType.HasValue;
        }
    }

    private readonly struct HttpStatusCodeParser : IExceptionParser
    {
        public bool TryParse(ref ExceptionParsingContext context)
        {
            if (context.ExceptionType.HasValue || !context.StatusCode.HasValue)
            {
                return false;
            }

            var message = context.Exception.Message;
            context.ExceptionType = context.StatusCode switch
            {
                // 3xx Redirection
                HttpStatusCode.MultipleChoices => HandledChatExceptionType.NetworkError,
                HttpStatusCode.MovedPermanently => HandledChatExceptionType.NetworkError, // 301
                HttpStatusCode.Found => HandledChatExceptionType.NetworkError, // 302
                HttpStatusCode.SeeOther => HandledChatExceptionType.NetworkError, // 303
                HttpStatusCode.NotModified => HandledChatExceptionType.NetworkError, // 304
                HttpStatusCode.UseProxy => HandledChatExceptionType.NetworkError, // 305
                HttpStatusCode.TemporaryRedirect => HandledChatExceptionType.NetworkError, // 307
                HttpStatusCode.PermanentRedirect => HandledChatExceptionType.NetworkError, // 308

                // 4xx Client Errors
                HttpStatusCode.BadRequest => IExceptionParser.ParseMessage(message, HandledChatExceptionType.InvalidConfiguration), // 400
                HttpStatusCode.Unauthorized => IExceptionParser.ParseMessage(message, HandledChatExceptionType.InvalidApiKey), // 401
                HttpStatusCode.PaymentRequired => HandledChatExceptionType.QuotaExceeded, // 402
                HttpStatusCode.Forbidden => IExceptionParser.ParseMessage(message, HandledChatExceptionType.InvalidApiKey), // 403
                HttpStatusCode.NotFound => IExceptionParser.ParseMessage(message, HandledChatExceptionType.InvalidConfiguration), // 404
                HttpStatusCode.MethodNotAllowed => IExceptionParser.ParseMessage(message, HandledChatExceptionType.InvalidConfiguration), // 405
                HttpStatusCode.NotAcceptable => IExceptionParser.ParseMessage(message, HandledChatExceptionType.InvalidConfiguration), // 406
                HttpStatusCode.RequestTimeout => HandledChatExceptionType.Timeout, // 408
                HttpStatusCode.Conflict => IExceptionParser.ParseMessage(message, HandledChatExceptionType.InvalidConfiguration), // 409
                HttpStatusCode.Gone => HandledChatExceptionType.InvalidConfiguration, // 410
                HttpStatusCode.LengthRequired => HandledChatExceptionType.InvalidConfiguration, // 411
                HttpStatusCode.RequestEntityTooLarge => HandledChatExceptionType.InvalidConfiguration, // 413
                HttpStatusCode.RequestUriTooLong => HandledChatExceptionType.InvalidEndpoint, // 414
                HttpStatusCode.UnsupportedMediaType => HandledChatExceptionType.InvalidConfiguration, // 415
                HttpStatusCode.UnprocessableEntity => HandledChatExceptionType.InvalidConfiguration, // 422
                HttpStatusCode.TooManyRequests => HandledChatExceptionType.RateLimit, // 429

                // 5xx Server Errors
                HttpStatusCode.InternalServerError => IExceptionParser.ParseMessage(message, HandledChatExceptionType.ServiceUnavailable), // 500
                HttpStatusCode.NotImplemented => IExceptionParser.ParseMessage(message, HandledChatExceptionType.FeatureNotSupport), // 501
                HttpStatusCode.BadGateway => IExceptionParser.ParseMessage(message, HandledChatExceptionType.ServiceUnavailable), // 502
                HttpStatusCode.ServiceUnavailable => IExceptionParser.ParseMessage(message, HandledChatExceptionType.ServiceUnavailable), // 503
                HttpStatusCode.GatewayTimeout => HandledChatExceptionType.Timeout, // 504
                _ => null
            };
            return context.ExceptionType.HasValue;
        }
    }
}

public enum HandledFunctionInvokingExceptionType
{
    /// <summary>
    /// An unknown error occurred during function invocation.
    /// </summary>
    Unknown,

    /// <summary>
    /// An argument error occurred during function invocation.
    /// </summary>
    ArgumentError,

    /// <summary>
    /// A required argument is missing.
    /// </summary>
    ArgumentMissing,

    /// <summary>
    /// The specified function was not found.
    /// </summary>
    FunctionNotFound,

    /// <summary>
    /// The function returned an invalid result that cannot be processed.
    /// </summary>
    InvalidResult
}

/// <summary>
/// Represents exceptions that occur during function invocation.
/// </summary>
public sealed partial class HandledFunctionInvokingException : HandledSystemException
{
    public HandledFunctionInvokingExceptionType SubExceptionType { get; }

    private HandledFunctionInvokingException(
        Exception originalException,
        HandledFunctionInvokingExceptionType subType,
        HandledSystemExceptionType type,
        IDynamicLocaleKey? customFriendlyMessageKey = null,
        bool isExpected = true) : base(originalException, type, customFriendlyMessageKey, isExpected)
    {
        SubExceptionType = subType;
    }

    public HandledFunctionInvokingException(
        HandledFunctionInvokingExceptionType type,
        string name,
        Exception? customException = null,
        IDynamicLocaleKey? customFriendlyMessageKey = null) : this(
        customException ?? MakeException(type, name),
        type,
        HandledSystemExceptionType.FunctionInvoking,
        customFriendlyMessageKey ?? MakeFriendlyMessageKey(type, name))
    {
    }

    private static Exception MakeException(HandledFunctionInvokingExceptionType type, string name) => type switch
    {
        HandledFunctionInvokingExceptionType.ArgumentError => new ArgumentException("Invalid argument provided.", name),
        HandledFunctionInvokingExceptionType.ArgumentMissing => new ArgumentException("Missing required argument.", name),
        HandledFunctionInvokingExceptionType.FunctionNotFound => new InvalidOperationException($"Function '{name}' not found."),
        HandledFunctionInvokingExceptionType.InvalidResult => new InvalidOperationException($"Function '{name}' returned an invalid result."),
        _ => new Exception("An unknown function invoking error occurred.")
    };

    private static FormattedDynamicLocaleKey? MakeFriendlyMessageKey(HandledFunctionInvokingExceptionType type, string name) => type switch
    {
        HandledFunctionInvokingExceptionType.ArgumentError => new FormattedDynamicLocaleKey(
            new DynamicLocaleKey(LocaleKey.HandledFunctionInvokingException_ArgumentError),
            new DirectLocaleKey(name)),
        HandledFunctionInvokingExceptionType.ArgumentMissing => new FormattedDynamicLocaleKey(
            new DynamicLocaleKey(LocaleKey.HandledFunctionInvokingException_ArgumentMissing),
            new DirectLocaleKey(name)),
        HandledFunctionInvokingExceptionType.FunctionNotFound => new FormattedDynamicLocaleKey(
            new DynamicLocaleKey(LocaleKey.HandledFunctionInvokingException_FunctionNotFound),
            new DirectLocaleKey(name)),
        HandledFunctionInvokingExceptionType.InvalidResult => new FormattedDynamicLocaleKey(
            new DynamicLocaleKey(LocaleKey.HandledFunctionInvokingException_InvalidResult),
            new DirectLocaleKey(name)),
        _ => null, // HandledSystemException will use its own Unknown key
    };

    public static Exception Handle(Exception exception)
    {
        if (exception is KernelException kernelException)
        {
            if (MissingArgumentRegex().Match(kernelException.Message) is { Success: true } match)
            {
                var paramName = match.Groups[1].Value;
                return new HandledFunctionInvokingException(
                    HandledFunctionInvokingExceptionType.ArgumentMissing,
                    paramName,
                    exception);
            }
        }

        return Handle(exception, true);
    }

    // Match `Missing argument for function parameter 'paramName'`
    [GeneratedRegex(@"Missing argument for function parameter '(.+?)'", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MissingArgumentRegex();
}