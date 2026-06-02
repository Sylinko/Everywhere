using Everywhere.I18N;

namespace Everywhere.Media.Ocr;

public sealed record OcrEngineDescriptor(
    IDynamicResourceKey NameKey,
    IDynamicResourceKey? DescriptionKey,
    bool IsOffline,
    bool IsSystemNative
);