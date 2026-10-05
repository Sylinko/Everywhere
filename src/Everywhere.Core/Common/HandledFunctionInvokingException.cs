using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;

namespace Everywhere.Common;

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