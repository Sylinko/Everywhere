using System.Runtime.InteropServices;
using Avalonia.Controls.Notifications;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Everywhere.Media.SpeechRecognition.Sherpa;

public sealed class SherpaOnnxSpeechRecognitionEngine(
    SherpaOnnxSpeechRecognitionEngineSettings settings,
    SherpaOnnxModelRegistry registry,
    SherpaOnnxModelInstaller installer,
    IKeyValueStorage keyValueStorage,
    ILogger<SherpaOnnxSpeechRecognitionEngine> logger
) : ISpeechRecognitionEngine, IHaveSettingsItems
{
    public string Id => "sherpa-onnx";

    public SpeechRecognitionEngineDescriptor Descriptor { get; } = new(
        new DynamicResourceKey(LocaleKey.SherpaOnnxSpeechRecognitionEngine_Name),
        new DynamicResourceKey(LocaleKey.SherpaOnnxSpeechRecognitionEngine_Description),
        true,
        false);

    public bool IsSupported { get; private set; }

    public IReadOnlyList<LocaleName> SupportedLocales { get; private set; } = [LocaleName.En, LocaleName.ZhHans];

    public IReadOnlyBindableList<DynamicNotification> Notifications => _notificationManager.Notifications;

    public SettingsItems SettingsItems => settings.SettingsItems;

    private readonly DynamicNotificationManager _notificationManager = new(keyValueStorage, "SpeechRecognition.SherpaOnnx");

    public Task InitializeAsync()
    {
        IsSupported = IsCurrentRuntimeSupported();

        if (!IsSupported)
        {
            _notificationManager.Push(
                "unsupported_runtime",
                new DynamicResourceKey(LocaleKey.SherpaOnnxSpeechRecognitionEngine_Notification_UnsupportedRuntime),
                NotificationType.Error,
                false);
            return Task.CompletedTask;
        }

        try
        {
            _ = VersionInfo.Version;
            SupportedLocales = registry.GetDefaultModel().SupportedLocales;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load sherpa-onnx native runtime.");

            IsSupported = false;
            _notificationManager.Push(
                "native_load_failed",
                new DynamicResourceKey(LocaleKey.SherpaOnnxSpeechRecognitionEngine_Notification_NativeLoadFailed),
                NotificationType.Error,
                false);
        }

        return Task.CompletedTask;
    }

    public async Task<ISpeechRecognitionSession> CreateSessionAsync(LocaleName locale, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            throw new NotSupportedException("sherpa-onnx speech recognition is not supported on this runtime.");
        }

        var metadata = registry.GetModel(settings.ModelId);
        if (!metadata.SupportedLocales.Contains(locale))
        {
            logger.LogDebug(
                "Locale {Locale} is not explicitly supported by sherpa-onnx model {ModelId}; using bilingual model anyway.",
                locale,
                metadata.Id);
        }

        var installState = await installer.RefreshStateAsync(metadata.Id, cancellationToken).ConfigureAwait(false);
        if (installState != SherpaOnnxModelInstallState.Installed)
        {
            throw new SherpaOnnxModelUnavailableException(metadata.Id, installState);
        }

        var modelRoot = installer.GetInstalledModelPath(metadata);
        var recognizer = CreateRecognizer(metadata, modelRoot, settings.ThreadCount);
        return new SherpaOnnxSpeechRecognitionSession(settings.MicrophoneDeviceId, metadata, recognizer);
    }

    private static OnlineRecognizer CreateRecognizer(SherpaOnnxModelMetadata metadata, string modelRoot, int threadCount)
    {
        var options = metadata.RuntimeOptions;
        if (metadata.RequiredFiles is not SherpaOnnxRequiredFiles.Transducer transducerFiles)
        {
            throw new NotSupportedException($"Unsupported sherpa-onnx online model family: {metadata.RequiredFiles.GetType().Name}.");
        }

        var config = new OnlineRecognizerConfig
        {
            FeatConfig = new FeatureConfig
            {
                SampleRate = options.SampleRate,
                FeatureDim = options.FeatureDim
            },
            ModelConfig = new OnlineModelConfig
            {
                Transducer = new OnlineTransducerModelConfig
                {
                    Encoder = Path.Combine(modelRoot, transducerFiles.Encoder),
                    Decoder = Path.Combine(modelRoot, transducerFiles.Decoder),
                    Joiner = Path.Combine(modelRoot, transducerFiles.Joiner)
                },
                Tokens = Path.Combine(modelRoot, transducerFiles.Tokens),
                NumThreads = threadCount == 0 ? Environment.ProcessorCount : threadCount,
                Provider = options.Provider,
                Debug = 0
            },
            DecodingMethod = options.DecodingMethod,
            MaxActivePaths = options.MaxActivePaths,
            EnableEndpoint = options.EnableEndpoint ? 1 : 0,
            Rule1MinTrailingSilence = options.Rule1MinTrailingSilence,
            Rule2MinTrailingSilence = options.Rule2MinTrailingSilence,
            Rule3MinUtteranceLength = options.Rule3MinUtteranceLength,
            HotwordsFile = options.HotwordsFile ?? string.Empty,
            HotwordsScore = options.HotwordsScore
        };

        return new OnlineRecognizer(config);
    }

    private static bool IsCurrentRuntimeSupported()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.OSArchitecture == Architecture.X64;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64;
        }

        return false;
    }
}
