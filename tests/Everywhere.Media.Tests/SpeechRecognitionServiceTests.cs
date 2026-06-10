using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Media.Audio;
using Everywhere.Media.SpeechRecognition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Everywhere.Media.Tests;

[TestFixture]
public class SpeechRecognitionServiceTests
{
    [Test]
    public async Task TryCreateInputState_WhenAlreadyActive_ReturnsNull()
    {
        var service = CreateService(CreateSettings(), new FakeSpeechRecognitionEngine());
        await service.InitializeAsync();

        var first = service.TryCreateInputState(SpeechRecognitionActivationKind.Toggle);
        var second = service.TryCreateInputState(SpeechRecognitionActivationKind.Hold);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Null);
        }
    }

    [Test]
    public async Task StartSpeechRecognitionAsync_ProcessesHypothesisAndFinal()
    {
        var session = new FakeSpeechRecognitionSession();
        var service = CreateService(CreateSettings(), new FakeSpeechRecognitionEngine(session));
        await service.InitializeAsync();
        var maybeState = service.TryCreateInputState(SpeechRecognitionActivationKind.Toggle);
        Assert.That(maybeState, Is.Not.Null);
        var state = maybeState!;
        List<string> commits = [];
        state.CommitRequested += (_, text) => commits.Add(text);

        var task = service.StartSpeechRecognitionAsync(state);
        session.Publish(new SpeechRecognitionUpdate.Started());
        session.Publish(new SpeechRecognitionUpdate.Hypothesis("hel"));
        session.Publish(new SpeechRecognitionUpdate.Final("hello"));
        session.Publish(new SpeechRecognitionUpdate.Completed());
        session.CompleteUpdates();
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(commits, Is.EqualTo(["hello"]));
            Assert.That(state.Composition, Is.Null);
            Assert.That(state.IsActive, Is.False);
            Assert.That(service.Status, Is.EqualTo(SpeechRecognitionStatus.Standby));
            Assert.That(service.TryCreateInputState(SpeechRecognitionActivationKind.Hold), Is.Not.Null);
        }
    }

    [Test]
    public async Task StopSpeechRecognitionAsync_CompletesSessionAndReleasesActiveSlot()
    {
        var session = new FakeSpeechRecognitionSession();
        var service = CreateService(CreateSettings(), new FakeSpeechRecognitionEngine(session));
        await service.InitializeAsync();
        var maybeState = service.TryCreateInputState(SpeechRecognitionActivationKind.Hold);
        Assert.That(maybeState, Is.Not.Null);
        var state = maybeState!;

        var task = service.StartSpeechRecognitionAsync(state);
        session.Publish(new SpeechRecognitionUpdate.Started());
        await SpinUntilAsync(() => service.Status == SpeechRecognitionStatus.Recording);

        await service.StopSpeechRecognitionAsync(state);
        await service.StopSpeechRecognitionAsync(state);
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.CompleteCount, Is.EqualTo(1));
            Assert.That(state.IsActive, Is.False);
            Assert.That(service.TryCreateInputState(SpeechRecognitionActivationKind.Toggle), Is.Not.Null);
        }
    }

    [Test]
    public async Task StopSpeechRecognitionAsync_CommitsPendingHypothesis()
    {
        var session = new FakeSpeechRecognitionSession();
        var service = CreateService(CreateSettings(), new FakeSpeechRecognitionEngine(session));
        await service.InitializeAsync();
        var maybeState = service.TryCreateInputState(SpeechRecognitionActivationKind.Toggle);
        Assert.That(maybeState, Is.Not.Null);
        var state = maybeState!;
        List<string> commits = [];
        state.CommitRequested += (_, text) => commits.Add(text);

        var task = service.StartSpeechRecognitionAsync(state);
        session.Publish(new SpeechRecognitionUpdate.Started());
        session.Publish(new SpeechRecognitionUpdate.Hypothesis("hello"));
        await SpinUntilAsync(() => state.Composition == "hello");

        await service.StopSpeechRecognitionAsync(state);
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(commits, Is.EqualTo(["hello"]));
            Assert.That(state.Composition, Is.Null);
            Assert.That(state.IsActive, Is.False);
        }
    }

    [Test]
    public async Task StartSpeechRecognitionAsync_WhenEngineThrows_StoresExceptionAndReleasesActiveSlot()
    {
        var engine = new FakeSpeechRecognitionEngine
        {
            CreateSessionException = new InvalidOperationException("boom")
        };
        var service = CreateService(CreateSettings(), engine);
        await service.InitializeAsync();
        var maybeState = service.TryCreateInputState(SpeechRecognitionActivationKind.Toggle);
        Assert.That(maybeState, Is.Not.Null);
        var state = maybeState!;

        await service.StartSpeechRecognitionAsync(state);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.LastException, Is.Not.Null);
            Assert.That(state.IsActive, Is.False);
            Assert.That(service.TryCreateInputState(SpeechRecognitionActivationKind.Hold), Is.Not.Null);
        }
    }

    [Test]
    public async Task StartSpeechRecognitionAsync_WithCustomHostedSession_ConsumesMicrophoneFrames()
    {
        var session = new FakeCustomHostedSpeechRecognitionSession();
        var microphoneDeviceManager = new FakeMicrophoneDeviceManager();
        var service = CreateService(CreateSettings(), microphoneDeviceManager, new FakeSpeechRecognitionEngine(session));
        await service.InitializeAsync();
        var maybeState = service.TryCreateInputState(SpeechRecognitionActivationKind.Hold);
        Assert.That(maybeState, Is.Not.Null);
        var state = maybeState!;
        List<string> commits = [];
        state.CommitRequested += (_, text) => commits.Add(text);

        await service.StartSpeechRecognitionAsync(state);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(microphoneDeviceManager.Capture.StartCount, Is.EqualTo(1));
            Assert.That(session.ConsumedFrames, Is.EqualTo(1));
            Assert.That(commits, Is.EqualTo(["hello"]));
            Assert.That(state.IsActive, Is.False);
        }
    }

    private static Settings CreateSettings() => new(new ServiceCollection().BuildServiceProvider());

    private static SpeechRecognitionService CreateService(Settings settings, params ISpeechRecognitionEngine[] engines) =>
        CreateService(settings, new FakeMicrophoneDeviceManager(), engines);

    private static SpeechRecognitionService CreateService(
        Settings settings,
        IMicrophoneDeviceManager microphoneDeviceManager,
        params ISpeechRecognitionEngine[] engines)
    {
        var services = new ServiceCollection();
        foreach (var engine in engines)
        {
            services.AddSingleton(engine);
        }

        return new SpeechRecognitionService(
            settings,
            services.BuildServiceProvider(),
            microphoneDeviceManager,
            Substitute.For<ILogger<SpeechRecognitionService>>());
    }

    private static async Task SpinUntilAsync(Func<bool> predicate)
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            cancellationTokenSource.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationTokenSource.Token);
        }
    }

    private sealed class FakeSpeechRecognitionEngine(ISpeechRecognitionSession? session = null) : ISpeechRecognitionEngine
    {
        private readonly ISpeechRecognitionSession _session = session ?? new FakeSpeechRecognitionSession();

        public string Id => "fake";

        public SpeechRecognitionEngineDescriptor Descriptor { get; } = new(
            new DirectResourceKey("Fake"),
            new DirectResourceKey("Fake"),
            true,
            true);

        public bool IsSupported { get; init; } = true;

        public IReadOnlyList<LocaleName> SupportedLocales { get; } = [LocaleName.En];

        public IReadOnlyBindableList<DynamicNotification> Notifications { get; } = new BindableList<DynamicNotification>();

        public Exception? CreateSessionException { get; init; }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<ISpeechRecognitionSession> CreateSessionAsync(LocaleName locale, CancellationToken cancellationToken = default)
        {
            if (CreateSessionException is not null) throw CreateSessionException;
            return Task.FromResult(_session);
        }
    }

    private sealed class FakeSpeechRecognitionSession : ISystemHostedSpeechRecognitionSession
    {
        private readonly Channel<SpeechRecognitionUpdate> _updates = Channel.CreateUnbounded<SpeechRecognitionUpdate>();

        public int CompleteCount { get; private set; }

        public void Publish(SpeechRecognitionUpdate update) => _updates.Writer.TryWrite(update);

        public void CompleteUpdates() => _updates.Writer.TryComplete();

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            CompleteCount++;
            _updates.Writer.TryWrite(new SpeechRecognitionUpdate.Completed());
            _updates.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<SpeechRecognitionUpdate> RecognizeAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken))
            {
                yield return update;
            }
        }

        public ValueTask DisposeAsync()
        {
            _updates.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCustomHostedSpeechRecognitionSession : ICustomHostedSpeechRecognitionSession
    {
        public int ConsumedFrames { get; private set; }

        public MicrophoneCaptureOptions MicrophoneCaptureOptions => new(
            DeviceId: null,
            SampleRate: 16000,
            Channels: 1,
            FramesPerBuffer: 1600);

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<SpeechRecognitionUpdate> RecognizeAsync(
            IAsyncEnumerable<AudioFrame> input,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new SpeechRecognitionUpdate.Started();
            await foreach (var _ in input.WithCancellation(cancellationToken))
            {
                ConsumedFrames++;
                yield return new SpeechRecognitionUpdate.Hypothesis("hel");
                yield return new SpeechRecognitionUpdate.Final("hello");
                break;
            }

            yield return new SpeechRecognitionUpdate.Completed();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMicrophoneDeviceManager : IMicrophoneDeviceManager
    {
        public FakeMicrophoneCapture Capture { get; } = new();

        public IReadOnlyList<MicrophoneDeviceDescriptor> GetInputDevices() => [];

        public string? GetDefaultInputDeviceId() => null;

        public IMicrophoneCapture CreateCapture(string? deviceId = null) => Capture;
    }

    private sealed class FakeMicrophoneCapture : IMicrophoneCapture
    {
        private readonly Channel<AudioFrame> _frames = Channel.CreateUnbounded<AudioFrame>();

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public IAsyncEnumerable<AudioFrame> Frames => _frames.Reader.ReadAllAsync();

        public Task StartAsync(MicrophoneCaptureOptions options, CancellationToken cancellationToken)
        {
            StartCount++;
            _frames.Writer.TryWrite(new AudioFrame(16000, 1, new float[160]));
            _frames.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            _frames.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
