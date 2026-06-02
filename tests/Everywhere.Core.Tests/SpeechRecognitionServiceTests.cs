using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Media;
using Everywhere.Media.SpeechRecognition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Everywhere.Core.Tests;

[TestFixture]
public class SpeechRecognitionServiceTests
{
    [Test]
    public async Task TryCreateInputState_WhenDisabled_ReturnsNull()
    {
        var engine = new FakeSpeechRecognitionEngine();
        var settings = new Settings();
        settings.SpeechRecognition.IsEnabled = false;
        var service = CreateService(settings, engine);
        await service.InitializeAsync();

        var state = service.TryCreateInputState(SpeechRecognitionActivationKind.Toggle);

        Assert.That(state, Is.Null);
    }

    [Test]
    public async Task TryCreateInputState_WhenAlreadyActive_ReturnsNull()
    {
        var service = CreateService(new Settings(), new FakeSpeechRecognitionEngine());
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
        var service = CreateService(new Settings(), new FakeSpeechRecognitionEngine(session));
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
        var service = CreateService(new Settings(), new FakeSpeechRecognitionEngine(session));
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
        var service = CreateService(new Settings(), new FakeSpeechRecognitionEngine(session));
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
        var service = CreateService(new Settings(), engine);
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

    private static SpeechRecognitionService CreateService(Settings settings, params ISpeechRecognitionEngine[] engines)
    {
        var services = new ServiceCollection();
        foreach (var engine in engines)
        {
            services.AddSingleton(engine);
        }

        return new SpeechRecognitionService(
            settings,
            services.BuildServiceProvider(),
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

    private sealed class FakeSpeechRecognitionEngine : ISpeechRecognitionEngine
    {
        private readonly FakeSpeechRecognitionSession _session;

        public FakeSpeechRecognitionEngine(FakeSpeechRecognitionSession? session = null)
        {
            _session = session ?? new FakeSpeechRecognitionSession();
        }

        public string Id { get; } = "fake";

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
            return Task.FromResult<ISpeechRecognitionSession>(_session);
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
}
