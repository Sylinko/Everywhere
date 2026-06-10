namespace Everywhere.Media.SpeechRecognition.Sherpa;

public sealed class SherpaOnnxModelRegistry
{
    public const string DefaultModelId = "sherpa-onnx-streaming-zipformer-ar_en_id_ja_ru_th_vi_zh-2025-02-10";

    private const string SmallBilingualZhEnModelId = "sherpa-onnx-streaming-zipformer-small-bilingual-zh-en-2023-02-16";
    private const string Chinese14MModelId = "sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23";
    private const string English20MModelId = "sherpa-onnx-streaming-zipformer-en-20M-2023-02-17";

    public IReadOnlyList<SherpaOnnxModelMetadata> Models { get; } =
    [
        new(
            Id: DefaultModelId,
            DisplayName: "Sherpa ONNX Streaming Zipformer 8-language",
            SupportedLocales:
            [
                LocaleName.En,
                LocaleName.Ja,
                LocaleName.Ru,
                LocaleName.ZhHans
            ],
            ArchiveFileName: $"{DefaultModelId}.tar.bz2",
            ArchiveSize: 258_986_573L,
            ArchiveSha256: "c363a8a3561edce3e6c127b44b76be12f2e3144d1ac865329316f44f0272cb94",
            RequiredFiles: new SherpaOnnxRequiredFiles.Transducer(
                Encoder: "encoder-epoch-75-avg-11-chunk-16-left-128.int8.onnx",
                Decoder: "decoder-epoch-75-avg-11-chunk-16-left-128.onnx",
                Joiner: "joiner-epoch-75-avg-11-chunk-16-left-128.int8.onnx",
                Tokens: "tokens.txt",
                BpeModel: "bpe.model"),
            RuntimeOptions: DefaultRuntimeOptions,
            Mirrors: MakeMirrors(DefaultModelId)),

        new(
            Id: SmallBilingualZhEnModelId,
            DisplayName: "sherpa-onnx streaming Zipformer small bilingual zh-en",
            SupportedLocales: [LocaleName.En, LocaleName.ZhHans],
            ArchiveFileName: $"{SmallBilingualZhEnModelId}.tar.bz2",
            ArchiveSize: 458_187_351L,
            ArchiveSha256: "38be47048f36b892c8cb80f0d3ac35d85d11f4c31ad0b76eb0822f82edaea3f9",
            RequiredFiles: new SherpaOnnxRequiredFiles.Transducer(
                Encoder: "encoder-epoch-99-avg-1.int8.onnx",
                Decoder: "decoder-epoch-99-avg-1.onnx",
                Joiner: "joiner-epoch-99-avg-1.int8.onnx",
                Tokens: "tokens.txt"),
            RuntimeOptions: DefaultRuntimeOptions,
            Mirrors: MakeMirrors(SmallBilingualZhEnModelId)),

        new(
            Id: Chinese14MModelId,
            DisplayName: "Sherpa ONNX Streaming Zipformer Chinese 14M",
            SupportedLocales: [LocaleName.ZhHans],
            ArchiveFileName: $"{Chinese14MModelId}.tar.bz2",
            ArchiveSize: 74_004_050L,
            ArchiveSha256: "2cbd71b640d9c37d3784f29367333a4577b0398b62e9deeed418170b081cba8b",
            RequiredFiles: new SherpaOnnxRequiredFiles.Transducer(
                Encoder: "encoder-epoch-99-avg-1.int8.onnx",
                Decoder: "decoder-epoch-99-avg-1.onnx",
                Joiner: "joiner-epoch-99-avg-1.int8.onnx",
                Tokens: "tokens.txt"),
            RuntimeOptions: DefaultRuntimeOptions,
            Mirrors: MakeMirrors(Chinese14MModelId)),

        new(
            Id: English20MModelId,
            DisplayName: "Sherpa ONNX Streaming Zipformer English 20M",
            SupportedLocales: [LocaleName.En],
            ArchiveFileName: $"{English20MModelId}.tar.bz2",
            ArchiveSize: 127_887_156L,
            ArchiveSha256: "9c559283e8498d3fe95913c79ca1cb454bb26281ac2b102b41306c7d752765d9",
            RequiredFiles: new SherpaOnnxRequiredFiles.Transducer(
                Encoder: "encoder-epoch-99-avg-1.int8.onnx",
                Decoder: "decoder-epoch-99-avg-1.onnx",
                Joiner: "joiner-epoch-99-avg-1.int8.onnx",
                Tokens: "tokens.txt"),
            RuntimeOptions: DefaultRuntimeOptions,
            Mirrors: MakeMirrors(English20MModelId))
    ];

    private static SherpaOnnxRuntimeOptions DefaultRuntimeOptions => new(
        SampleRate: 16000,
        FeatureDim: 80,
        DecodingMethod: "greedy_search",
        MaxActivePaths: 4,
        ThreadCount: 2,
        Provider: "cpu",
        EnableEndpoint: true,
        Rule1MinTrailingSilence: 2.4f,
        Rule2MinTrailingSilence: 1.2f,
        Rule3MinUtteranceLength: 20f);

    public SherpaOnnxModelMetadata GetDefaultModel() => GetModel(DefaultModelId);

    public SherpaOnnxModelMetadata GetModel(string? modelId)
    {
        var id = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId;
        return Models.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Models[0];
    }

    private static IReadOnlyList<SherpaOnnxModelMirror> MakeMirrors(string modelId) =>
    [
        new(
            "huggingface",
            $"https://huggingface.co/xumo/onnx_models/resolve/main/{modelId}.tar.bz2"),
        new(
            "github",
            $"https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/{modelId}.tar.bz2"),
        new(
            "hf-mirror",
            $"https://hf-mirror.com/xumo/onnx_models/resolve/main/{modelId}.tar.bz2")
    ];
}