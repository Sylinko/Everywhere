using Everywhere.I18N;

namespace Everywhere.Media.ImageRecognition;

public sealed record ImageTextRecognitionEngineDescriptor(
    IDynamicResourceKey NameKey,
    IDynamicResourceKey? DescriptionKey,
    bool IsOffline,
    bool IsSystemNative
);