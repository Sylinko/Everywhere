using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows.Input;
using Windows.Devices.Enumeration;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Media.SpeechRecognition;
using Everywhere.Windows.Extensions;
using Microsoft.Extensions.Logging;
using Windows.Media.SpeechRecognition;
using Serilog;
using ZLinq;

namespace Everywhere.Windows.Media;

public sealed class WinRTSpeechRecognitionEngine : ISpeechRecognitionEngine, IDisposable
{
    public string Id => "winrt";

    public SpeechRecognitionEngineDescriptor Descriptor { get; } = new(
        new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Name),
        new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Description),
        false,
        true);

    public bool IsSupported => SupportedLocales.Count > 0;

    public IReadOnlyList<LocaleName> SupportedLocales { get; }

    public IReadOnlyBindableList<DynamicNotification> Notifications => _notificationManager.Notifications;

    private readonly DynamicNotificationManager _notificationManager;

    public WinRTSpeechRecognitionEngine(IKeyValueStorage keyValueStorage)
    {
        _notificationManager = new DynamicNotificationManager(keyValueStorage, "SpeechRecognition.WinRT");

        SupportedLocales = SpeechRecognizer.SupportedTopicLanguages?
            .AsValueEnumerable()
            .Select(WinRTExtensions.ToLocaleName)
            .Distinct()
            .ToList() ?? [];

        if (IsSupported)
        {
            // We cannot check whether the user has accepted the speech privacy policy in System Settings.
            // Just show a notification to remind the user to check the speech privacy policy if speech recognition is supported.
            PushEnablePrivacyPolicyNotification(false);
        }
        else
        {
            PushNoSupportedLanguagesNotification();
        }
    }

    public async Task InitializeAsync()
    {
        DeviceInformationCollection? devices;

        try
        {
            devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
        }
        catch (Exception ex)
        {
            Log.ForContext<WinRTSpeechRecognitionEngine>().Error(ex, "Failed to enumerate audio capture devices.");
            return;
        }

        if (devices is not { Count: > 0 })
        {
            _notificationManager.Push(
                new DynamicNotificationDescriptor(
                    "no_microphone",
                    new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Notification_NoMicrophone),
                    NotificationType.Warning,
                    ActionButtonContentKey: new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Notification_OpenSettings),
                    ActionCommand: OpenSoundSettingsCommand));
        }
    }

    public async Task<ISpeechRecognitionSession> CreateSessionAsync(LocaleName locale, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            PushNoSupportedLanguagesNotification();
            throw new NotSupportedException("Speech recognition is not supported on this system.");
        }

        var language = locale.ToWinRTLanguage();
        if (SpeechRecognizer.SupportedTopicLanguages is not { } supportedLanguages ||
            !supportedLanguages.AsValueEnumerable().Any(x => string.Equals(
                x.LanguageTag,
                language.LanguageTag,
                StringComparison.OrdinalIgnoreCase)))
        {
            PushUnsupportedLocaleNotification(locale);
            throw new NotSupportedException($"Speech recognition does not support the specified locale: {locale}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var recognizer = new SpeechRecognizer(language);

        try
        {
            recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));

            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await recognizer.CompileConstraintsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (compilation?.Status != SpeechRecognitionResultStatus.Success)
            {
                PushConstraintCompilationNotification(compilation?.Status.ToString() ?? "Unknown");
                throw new InvalidOperationException($"Failed to compile speech recognition constraints: {compilation?.Status}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            recognizer.Dispose();
            PushConstraintCompilationNotification(ex.Message);
            throw;
        }
        catch
        {
            recognizer.Dispose();
            throw;
        }

        return new Session(this, recognizer);
    }

    private void PushEnablePrivacyPolicyNotification(bool isRequired)
    {
        _notificationManager.Push(
            new DynamicNotificationDescriptor(
                isRequired ? "enable_privacy_policy" : "check_privacy_policy",
                new DynamicResourceKey(
                    isRequired ?
                        LocaleKey.WinRTSpeechRecognitionEngine_Notification_EnableSpeechPrivacyPolicy :
                        LocaleKey.WinRTSpeechRecognitionEngine_Notification_CheckSpeechPrivacyPolicy),
                isRequired ? NotificationType.Error : NotificationType.Warning,
                ActionButtonContentKey: new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Notification_OpenSettings),
                ActionCommand: OpenSpeechPrivacySettingsCommand));
    }

    private void PushNoSupportedLanguagesNotification()
    {
        _notificationManager.Push(
            new DynamicNotificationDescriptor(
                "no_supported_languages",
                new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Notification_NoSupportedLanguages),
                NotificationType.Warning,
                ActionButtonContentKey: new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Notification_OpenSettings),
                ActionCommand: OpenLanguageSettingsCommand));
    }

    private void PushUnsupportedLocaleNotification(LocaleName locale)
    {
        _notificationManager.Push(
            new DynamicNotificationDescriptor(
                $"unsupported_locale_{locale}",
                new FormattedDynamicResourceKey(
                    LocaleKey.WinRTSpeechRecognitionEngine_Notification_UnsupportedLocale,
                    new DirectResourceKey(locale.ToString())),
                NotificationType.Warning,
                ForceShow: true,
                ActionButtonContentKey: new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Notification_OpenSettings),
                ActionCommand: OpenLanguageSettingsCommand));
    }

    private void PushConstraintCompilationNotification(string detail)
    {
        _notificationManager.Push(
            new DynamicNotificationDescriptor(
                "constraint_compilation_failed",
                new FormattedDynamicResourceKey(
                    LocaleKey.WinRTSpeechRecognitionEngine_Notification_ConstraintCompilationFailed,
                    new DirectResourceKey(detail)),
                NotificationType.Error,
                ForceShow: true,
                ActionButtonContentKey: new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Notification_OpenSettings),
                ActionCommand: OpenSpeechSettingsCommand));
    }

    private void PushRecognitionStatusNotification(SpeechRecognitionResultStatus status)
    {
        switch (status)
        {
            case SpeechRecognitionResultStatus.MicrophoneUnavailable:
                _notificationManager.Push(
                    new DynamicNotificationDescriptor(
                        "microphone_unavailable",
                        new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Notification_MicrophoneUnavailable),
                        NotificationType.Warning,
                        ForceShow: true,
                        ActionButtonContentKey: new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Notification_OpenSettings),
                        ActionCommand: OpenMicrophonePrivacySettingsCommand));
                break;
            case SpeechRecognitionResultStatus.TopicLanguageNotSupported:
                _notificationManager.Push(
                    new DynamicNotificationDescriptor(
                        "topic_language_not_supported",
                        new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Notification_TopicLanguageNotSupported),
                        NotificationType.Warning,
                        ForceShow: true,
                        ActionButtonContentKey: new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Notification_OpenSettings),
                        ActionCommand: OpenLanguageSettingsCommand));
                break;
            case SpeechRecognitionResultStatus.NetworkFailure:
                _notificationManager.Push(
                    new DynamicNotificationDescriptor(
                        "network_failure",
                        new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Notification_NetworkFailure),
                        NotificationType.Warning,
                        ForceShow: true,
                        ActionButtonContentKey: new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Notification_OpenSettings),
                        ActionCommand: OpenSpeechSettingsCommand));
                break;
        }
    }

    public void Dispose()
    {
        _notificationManager.Dispose();
    }

    public override bool Equals(object? obj) => obj is WinRTSpeechRecognitionEngine other && Id == other.Id;

    public override int GetHashCode() => Id.GetHashCode();

    private static ICommand OpenSoundSettingsCommand =>
        new AsyncRelayCommand(async () => await App.Launcher.LaunchUriAsync(new Uri("ms-settings:sound")));

    private static ICommand OpenSpeechSettingsCommand =>
        new AsyncRelayCommand(async () => await App.Launcher.LaunchUriAsync(new Uri("ms-settings:speech")));

    private static ICommand OpenLanguageSettingsCommand =>
        new AsyncRelayCommand(async () => await App.Launcher.LaunchUriAsync(new Uri("ms-settings:regionlanguage")));

    private static ICommand OpenSpeechPrivacySettingsCommand =>
        new AsyncRelayCommand(async () => await App.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-speech")));

    private static ICommand OpenMicrophonePrivacySettingsCommand =>
        new AsyncRelayCommand(async () => await App.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone")));

    private sealed class Session : ISystemHostedSpeechRecognitionSession
    {
        private readonly WinRTSpeechRecognitionEngine _engine;
        private readonly SpeechRecognizer _speechRecognizer;
        private readonly Channel<SpeechRecognitionUpdate> _updates = Channel.CreateUnbounded<SpeechRecognitionUpdate>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        private int _isCompleted;
        private int _isDisposed;

        public Session(WinRTSpeechRecognitionEngine engine, SpeechRecognizer speechRecognizer)
        {
            _engine = engine;
            _speechRecognizer = speechRecognizer;

            _speechRecognizer.HypothesisGenerated += (_, args) =>
            {
                var text = args.Hypothesis?.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _updates.Writer.TryWrite(new SpeechRecognitionUpdate.Hypothesis(text));
                }
            };

            _speechRecognizer.RecognitionQualityDegrading += (_, args) =>
            {
                _updates.Writer.TryWrite(
                    new SpeechRecognitionUpdate.Diagnostic(
                        $"Windows speech recognition quality is degrading: {args.Problem}.",
                        LogLevel.Debug));
            };

            _speechRecognizer.ContinuousRecognitionSession.ResultGenerated += (_, args) =>
            {
                var result = args.Result;

                if (result.Status == SpeechRecognitionResultStatus.Success && !string.IsNullOrWhiteSpace(result.Text))
                {
                    _updates.Writer.TryWrite(new SpeechRecognitionUpdate.Final(result.Text));

                    // _updates.Writer.TryWrite(
                    //     new SpeechRecognitionUpdate.Final(
                    //         result.Text,
                    //         MapConfidence(result.Confidence)));
                }
                else if (result.Status != SpeechRecognitionResultStatus.Success)
                {
                    engine.PushRecognitionStatusNotification(result.Status);

                    _updates.Writer.TryWrite(
                        new SpeechRecognitionUpdate.Diagnostic(
                            $"Windows speech recognition result status: {result.Status}.",
                            MapLogLevel(result.Status)));
                }
            };

            _speechRecognizer.ContinuousRecognitionSession.Completed += (_, args) =>
            {
                if (args.Status != SpeechRecognitionResultStatus.Success)
                {
                    engine.PushRecognitionStatusNotification(args.Status);

                    _updates.Writer.TryWrite(
                        new SpeechRecognitionUpdate.Diagnostic(
                            $"Windows speech recognition completed with status: {args.Status}.",
                            MapLogLevel(args.Status)));
                }

                _updates.Writer.TryWrite(new SpeechRecognitionUpdate.Completed());
                _updates.Writer.TryComplete();
            };
        }

        // private static float? MapConfidence(SpeechRecognitionConfidence confidence)
        // {
        //     return confidence switch
        //     {
        //         SpeechRecognitionConfidence.High => 0.9f,
        //         SpeechRecognitionConfidence.Medium => 0.65f,
        //         SpeechRecognitionConfidence.Low => 0.35f,
        //         SpeechRecognitionConfidence.Rejected => 0.0f,
        //         _ => null
        //     };
        // }

        private static LogLevel MapLogLevel(SpeechRecognitionResultStatus status)
        {
            return status switch
            {
                SpeechRecognitionResultStatus.Success => LogLevel.Information,
                SpeechRecognitionResultStatus.UserCanceled => LogLevel.Information,
                SpeechRecognitionResultStatus.TimeoutExceeded => LogLevel.Debug,
                SpeechRecognitionResultStatus.NetworkFailure => LogLevel.Warning,
                SpeechRecognitionResultStatus.MicrophoneUnavailable => LogLevel.Warning,
                SpeechRecognitionResultStatus.TopicLanguageNotSupported => LogLevel.Warning,
                _ => LogLevel.Information
            };
        }

        public async IAsyncEnumerable<SpeechRecognitionUpdate> RecognizeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _speechRecognizer.ContinuousRecognitionSession.StartAsync();
            }
            catch (COMException ex) when (ex.ErrorCode == unchecked((int)0x80045509))
            {
                // The speech privacy policy was not accepted prior to attempting a speech recognition.
                _engine.PushEnablePrivacyPolicyNotification(true);
                throw new HandledSystemException(
                    ex,
                    HandledSystemExceptionType.COMException,
                    new DynamicResourceKey(LocaleKey.WinRTSpeechRecognitionEngine_Notification_EnableSpeechPrivacyPolicy));
            }

            yield return new SpeechRecognitionUpdate.Started();

            await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken))
            {
                yield return update;
            }
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _isCompleted, 1) == 1) return;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _speechRecognizer.ContinuousRecognitionSession.StopAsync();
            }
            finally
            {
                _updates.Writer.TryComplete();
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 1) return default;

            _updates.Writer.TryComplete();
            _speechRecognizer.Dispose();
            return default;
        }
    }
}