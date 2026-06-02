using Everywhere.I18N;

namespace Everywhere.Media.SpeechRecognition;

public sealed record SpeechRecognitionEngineDescriptor(
    IDynamicResourceKey NameKey,
    IDynamicResourceKey? DescriptionKey,
    bool IsOffline,
    bool IsSystemNative
);