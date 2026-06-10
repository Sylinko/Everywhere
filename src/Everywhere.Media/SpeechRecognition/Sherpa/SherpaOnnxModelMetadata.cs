namespace Everywhere.Media.SpeechRecognition.Sherpa;

public sealed record SherpaOnnxModelMetadata(
    string Id,
    string DisplayName,
    IReadOnlyList<LocaleName> SupportedLocales,
    string ArchiveFileName,
    long ArchiveSize,
    string ArchiveSha256,
    SherpaOnnxRequiredFiles RequiredFiles,
    SherpaOnnxRuntimeOptions RuntimeOptions,
    IReadOnlyList<SherpaOnnxModelMirror> Mirrors
);

public abstract record SherpaOnnxRequiredFiles(string Tokens, string? BpeModel = null)
{
    public sealed record Transducer(
        string Encoder,
        string Decoder,
        string Joiner,
        string Tokens,
        string? BpeModel = null
    ) : SherpaOnnxRequiredFiles(Tokens, BpeModel);

    // ReSharper disable once IdentifierTypo
    public sealed record Zipformer2Ctc(
        string Model,
        string Tokens,
        string? BpeModel = null
    ) : SherpaOnnxRequiredFiles(Tokens, BpeModel);
}

public sealed record SherpaOnnxRuntimeOptions(
    int SampleRate,
    int FeatureDim,
    string DecodingMethod,
    int MaxActivePaths,
    int ThreadCount,
    string Provider,
    bool EnableEndpoint,
    float Rule1MinTrailingSilence,
    float Rule2MinTrailingSilence,
    float Rule3MinUtteranceLength,
    string? HotwordsFile = null,
    float HotwordsScore = 1.5f
);

public sealed record SherpaOnnxModelMirror(
    string SourceId,
    string Url
);

public enum SherpaOnnxModelInstallState
{
    NotInstalled,
    Downloading,
    DownloadFailed,
    Verifying,
    Installing,
    Installed,
    Corrupted,
    Unsupported,
    UpdateAvailable
}