using Microsoft.Extensions.Logging;

namespace Everywhere.Media.SpeechRecognition;

/// <summary>
/// Represents an update from the speech recognition engine during the recognition process.
/// </summary>
public abstract record SpeechRecognitionUpdate
{
    private SpeechRecognitionUpdate() { }

    public sealed record Started : SpeechRecognitionUpdate;

    public sealed record Hypothesis(string Text) : SpeechRecognitionUpdate;

    public sealed record Final(string Text) : SpeechRecognitionUpdate;

    public sealed record Reset : SpeechRecognitionUpdate;

    public sealed record Completed : SpeechRecognitionUpdate;

    public sealed record Diagnostic(string Message, LogLevel Level = LogLevel.Information) : SpeechRecognitionUpdate;
}