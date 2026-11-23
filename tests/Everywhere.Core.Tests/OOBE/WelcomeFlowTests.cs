using System.ComponentModel;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Cloud;
using Everywhere.Configuration;
using Everywhere.Core.I18N;
using Everywhere.Harness;
using Everywhere.I18N;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.Skills;
using Everywhere.ViewModels;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MEAI = Microsoft.Extensions.AI;

namespace Everywhere.Core.Tests.OOBE;

public sealed class WelcomeFlowTests
{
    [AvaloniaTest]
    public async Task OwnModelFlow_CompletesEndToEndWithoutPrematureVersionBumpAndSelectsAssistant()
    {
        using var harness = new TestHarness();
        using var flow = harness.CreateFlow();
        var celebrations = 0;
        flow.CelebrationRequested += (_, _) => celebrations++;

        // Stage 0: Intro -> Source
        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeIntroStep>());
        Assert.That(flow.CanContinue, Is.True);
        await flow.ContinueCommand.ExecuteAsync(null);

        // Stage 1: Source must be explicitly chosen before continuing
        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeSourceStep>());
        Assert.That(flow.SelectedSource, Is.Null);
        Assert.That(flow.Progress, Has.Count.EqualTo(8));
        Assert.That(flow.CanContinue, Is.False);
        flow.SelectSourceCommand.Execute(WelcomeModelSource.Own);
        Assert.That(flow.Progress, Has.Count.EqualTo(7));
        Assert.That(flow.Progress.Select(static item => item.Stage), Is.EqualTo([0, 1, 3, 4, 5, 6, 7]));
        Assert.That(flow.CanContinue, Is.True);
        await flow.ContinueCommand.ExecuteAsync(null);

        // Stage 3: Merged Configure (defaults to Built-in Providers tab; switch seamlessly to Custom Protocol tab)
        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeConfigurationStep>());
        Assert.That(flow.IsPresetMode, Is.True);
        flow.SelectModeCommand.Execute(true);
        Assert.That(flow.IsAdvancedOwnMode, Is.True);
        Assert.That(flow.Assistant.Configuration, Is.TypeOf<AdvancedAssistantConfiguration>());
        var advanced = (AdvancedAssistantConfiguration)flow.Assistant.Configuration;
        advanced.Endpoint = "https://api.example.com/v1";
        advanced.Schema = ModelProviderSchema.OpenAI;
        advanced.ModelId = "gpt-5-mini";

        // Test connection verifies, celebrates once, saves assistant once, and stays on Configure step.
        await flow.ContinueCommand.ExecuteAsync(null);
        Assert.Multiple(() =>
        {
            Assert.That(flow.IsConnectivityChecked, Is.True);
            Assert.That(celebrations, Is.EqualTo(1));
            Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeConfigurationStep>());
            Assert.That(harness.Settings.Model.CustomAssistants, Has.One.Items.EqualTo(flow.Assistant));
            Assert.That(harness.Settings.Model.SelectedCustomAssistant, Is.SameAs(flow.Assistant));
            Assert.That(harness.Settings.CompletedOobeVersion, Is.Zero);
        });

        // Second continue advances to Personalize
        await flow.ContinueCommand.ExecuteAsync(null);
        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomePersonalizationStep>());

        // Scenario selection is required before saving preferences, but validation stays hidden until user interaction
        Assert.That(flow.HasScenarios, Is.False);
        Assert.That(flow.ShowScenarioRequiredHint, Is.False);
        Assert.That(flow.CanContinue, Is.False);
        var scenario = flow.Personalization.ScenarioOptions.First(o => o.Id == "programming-development");
        var dataScenario = flow.Personalization.ScenarioOptions.First(o => o.Id == "data-analysis");
        var researchScenario = flow.Personalization.ScenarioOptions.First(o => o.Id == "research-planning");
        flow.Personalization.ToggleScenarioCommand.Execute(scenario);
        flow.Personalization.PreferredUserName = "Alice";
        Assert.That(flow.HasScenarios, Is.True);
        Assert.That(flow.ShowScenarioRequiredHint, Is.False);
        Assert.That(flow.CanContinue, Is.True);
        Assert.That(flow.PreviewQuestionKey.ToString(), Is.EqualTo(LocaleKey.Oobe_Preview_Question_Programming.I18N()));
        Assert.That(flow.PreviewReplyKey.ToString(), Does.Contain("Alice"));

        // Selecting persona and organization weaves them naturally into the assistant preview bubble
        var baselineReply = flow.PreviewReplyKey.ToString();
        var programmingPersona = flow.Personalization.PersonaOptions.First(o => o.Id == "programming");
        flow.Personalization.SelectPersonaCommand.Execute(programmingPersona);
        var personaReply = flow.PreviewReplyKey.ToString();
        Assert.That(personaReply, Is.Not.EqualTo(baselineReply));
        Assert.That(personaReply, Does.Contain(LocaleKey.Oobe_Preview_Persona_ProgrammingWrap.I18N().Split('{')[0]));

        var stepByStepOrganization = flow.Personalization.OrganizationOptions.First(o => o.Id == "step-by-step");
        flow.Personalization.SelectOrganizationCommand.Execute(stepByStepOrganization);
        var organizedReply = flow.PreviewReplyKey.ToString();
        Assert.That(organizedReply, Is.Not.EqualTo(personaReply));
        Assert.That(organizedReply, Does.Contain("1."));

        // Selecting 2nd and 3rd scenarios updates preview dynamically to the latest selected scenario
        flow.Personalization.ToggleScenarioCommand.Execute(dataScenario);
        Assert.That(flow.PreviewQuestionKey.ToString(), Is.EqualTo(LocaleKey.Oobe_Preview_Question_DataAnalysis.I18N()));
        flow.Personalization.ToggleScenarioCommand.Execute(researchScenario);
        Assert.That(flow.PreviewQuestionKey.ToString(), Is.EqualTo(LocaleKey.Oobe_Preview_Question_Research.I18N()));

        // Deselecting falls back to the prior selected scenario, and clearing all surfaces the required hint
        flow.Personalization.ToggleScenarioCommand.Execute(researchScenario);
        Assert.That(flow.PreviewQuestionKey.ToString(), Is.EqualTo(LocaleKey.Oobe_Preview_Question_DataAnalysis.I18N()));
        flow.Personalization.ToggleScenarioCommand.Execute(dataScenario);
        flow.Personalization.ToggleScenarioCommand.Execute(scenario);
        Assert.That(flow.HasScenarios, Is.False);
        Assert.That(flow.ShowScenarioRequiredHint, Is.True);
        flow.Personalization.ToggleScenarioCommand.Execute(scenario);
        Assert.That(flow.ShowScenarioRequiredHint, Is.False);

        await flow.ContinueCommand.ExecuteAsync(null);
        Assert.Multiple(() =>
        {
            Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeShortcutStep>());
            Assert.That(flow.Assistant.SystemPromptId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(harness.Settings.CompletedOobeVersion, Is.Zero);
        });

        // Clearing shortcut blocks Confirm, while Skip ("Don't use") remains available; Confirm enables shortcut
        harness.Settings.Shortcut.ChatWindow.IsEnabled = false;
        harness.Settings.Shortcut.ChatWindow.Main = default;
        Assert.Multiple(() =>
        {
            Assert.That(flow.CanContinue, Is.False);
            Assert.That(flow.CanSecondaryAction, Is.True);
        });
        harness.Settings.Shortcut.ChatWindow.Main = new KeyboardShortcut(Key.E, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.That(flow.CanContinue, Is.True);

        await flow.ContinueCommand.ExecuteAsync(null);
        Assert.Multiple(() =>
        {
            Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeTextSelectionStep>());
            Assert.That(harness.Settings.Shortcut.ChatWindow.IsEnabled, Is.True);
            Assert.That(harness.Settings.CompletedOobeVersion, Is.Zero);
            Assert.That(flow.HasPreviousStep, Is.True);
            Assert.That(flow.CanContinue, Is.True);
            Assert.That(flow.CanSecondaryAction, Is.True);
        });

        await flow.ContinueCommand.ExecuteAsync(null);
        Assert.Multiple(() =>
        {
            Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeFinishStep>());
            Assert.That(harness.Settings.TextSelection.IsToolbarEnabled, Is.True);
            Assert.That(harness.Settings.CompletedOobeVersion, Is.EqualTo(OobeCoordinator.CurrentVersion));
            Assert.That(flow.HasPreviousStep, Is.True);
        });

        // Final opt-out preserves DiagnosticData = false
        harness.Settings.Common.DiagnosticData = false;
        var closeCount = 0;
        flow.CloseRequested += (_, _) => closeCount++;
        await flow.ContinueCommand.ExecuteAsync(null);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Settings.Common.DiagnosticData, Is.False);
            Assert.That(flow.ShouldStartUsing, Is.True);
            Assert.That(closeCount, Is.EqualTo(1));
        });

        var activated = 0;
        var recipient = new MessageRecorder<ActivateChatSessionMessage>(() => activated++);
        WeakReferenceMessenger.Default.Register(recipient);
        try
        {
            flow.StartUsing();
            Assert.That(activated, Is.EqualTo(1));
            Assert.That(harness.Settings.Model.SelectedCustomAssistant, Is.SameAs(flow.Assistant));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [AvaloniaTest]
    public async Task ConfigurationEdits_WhenModifiedOrApiKeySecretEdited_InvalidateConnectivityAndPreventDuplicates()
    {
        using var harness = new TestHarness();
        using var flow = harness.CreateFlow();
        var apiKey = new ApiKey { Name = "Primary", SecretKey = "secret-1" };
        harness.Settings.Model.ApiKeys.Add(apiKey);

        flow.SelectSourceCommand.Execute(WelcomeModelSource.Own);
        flow.SelectModeCommand.Execute(true);
        await flow.ContinueCommand.ExecuteAsync(null); // -> Source
        await flow.ContinueCommand.ExecuteAsync(null); // -> Configure

        var advanced = (AdvancedAssistantConfiguration)flow.Assistant.Configuration;
        advanced.Endpoint = "https://api.example.com/v1";
        advanced.Schema = ModelProviderSchema.OpenAI;
        advanced.ModelId = "gpt-5-mini";
        advanced.ApiKey = apiKey.Id;

        await flow.ContinueCommand.ExecuteAsync(null);
        Assert.That(flow.IsConnectivityChecked, Is.True);

        // Editing referenced ApiKey SecretKey invalidates verified status
        apiKey.SecretKey = "secret-2";
        Dispatcher.UIThread.RunJobs();
        Assert.That(flow.IsConnectivityChecked, Is.False);

        // Re-verifying reuses the same assistant instance without adding duplicates
        await flow.ContinueCommand.ExecuteAsync(null);
        Assert.That(flow.IsConnectivityChecked, Is.True);
        Assert.That(harness.Settings.Model.CustomAssistants, Has.One.Items.EqualTo(flow.Assistant));
    }

    [AvaloniaTest]
    public async Task ConfigurationDrafts_WhenSwitchingSourceOrModeAndNavigatingBack_PreserveEnteredValues()
    {
        using var harness = new TestHarness();
        using var flow = harness.CreateFlow();

        flow.SelectSourceCommand.Execute(WelcomeModelSource.Own);
        flow.SelectModeCommand.Execute(true);
        var advanced = (AdvancedAssistantConfiguration)flow.Assistant.Configuration;
        advanced.Endpoint = "https://custom.local/v1";
        advanced.ModelId = "qwen3-coder";

        flow.SelectSourceCommand.Execute(WelcomeModelSource.Cloud);
        Assert.That(flow.Assistant.Configuration, Is.TypeOf<OfficialAssistantConfiguration>());
        Assert.That(flow.DisplayProviderHost, Is.EqualTo("Everywhere Cloud"));

        flow.SelectSourceCommand.Execute(WelcomeModelSource.Own);
        Assert.That(flow.Assistant.Configuration, Is.SameAs(advanced));
        Assert.That(((AdvancedAssistantConfiguration)flow.Assistant.Configuration).ModelId, Is.EqualTo("qwen3-coder"));
    }

    [AvaloniaTest]
    public async Task CloudRoute_RoutesThroughLoginOnlyWhenSignedOutAndSurfacesLoginFailures()
    {
        using var harness = new TestHarness();
        harness.CloudClient.LoginAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                harness.CloudClient.LastLoginErrorKey.Returns(new DynamicLocaleKey(LocaleKey.Oobe_LoginFailed));
                return Task.FromResult(false);
            });
        using var flow = harness.CreateFlow();

        await flow.ContinueCommand.ExecuteAsync(null); // Intro -> Source
        flow.SelectSourceCommand.Execute(WelcomeModelSource.Cloud);
        await flow.ContinueCommand.ExecuteAsync(null); // Source -> Login

        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeLoginStep>());
        Assert.That(flow.CanContinue, Is.True);
        Assert.That(flow.CurrentStep?.PrimaryLabelKey.ToString(), Is.EqualTo(LocaleKey.Oobe_Login_SignIn.I18N()));

        // Clicking footer Sign In invokes CloudClient.LoginAsync and stays on step with ErrorKey if still unsigned
        await flow.ContinueCommand.ExecuteAsync(null);
        Assert.Multiple(() =>
        {
            Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeLoginStep>());
            Assert.That(flow.ErrorKey?.ToString(), Is.EqualTo(LocaleKey.Oobe_LoginFailed.I18N()));
            Assert.That(flow.CanContinue, Is.True);
        });

        // Simulate successful sign-in event from CloudClient -> primary CTA switches to Continue
        harness.SetSignedIn(new UserProfile { Name = "Tester", Email = "tester@example.com" });
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(flow.IsSignedIn, Is.True);
            Assert.That(flow.CanContinue, Is.True);
            Assert.That(flow.CurrentStep?.PrimaryLabelKey.ToString(), Is.EqualTo(LocaleKey.Oobe_Continue.I18N()));
        });

        // Advances directly to Configure, skipping Mode
        await flow.ContinueCommand.ExecuteAsync(null);
        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeConfigurationStep>());

        // Moving back preserves visited Login step in history
        flow.MovePreviousCommand.Execute(null);
        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeLoginStep>());
    }

    [Test]
    public void CapabilityAndPreviewProjections_ReflectConfigurationAndPersonalizationChanges()
    {
        using var harness = new TestHarness();
        using var flow = harness.CreateFlow();

        flow.SelectSourceCommand.Execute(WelcomeModelSource.Own);
        flow.SelectModeCommand.Execute(true);
        var advanced = (AdvancedAssistantConfiguration)flow.Assistant.Configuration;
        advanced.Endpoint = "https://models.contoso.dev/v1";
        advanced.ModelId = "claude-4-sonnet";
        advanced.ContextLimit = 200_000;
        advanced.OutputLimit = 16_384;
        advanced.InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf;
        advanced.SupportsToolCall = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(flow.DisplayModelName, Is.EqualTo("claude-4-sonnet"));
            Assert.That(flow.DisplayProviderHost, Is.EqualTo("models.contoso.dev"));
            Assert.That(flow.ContextLimitText, Is.EqualTo("200K"));
            Assert.That(flow.OutputLimitText, Is.EqualTo("16.4K"));
            Assert.That(flow.SupportsImageInput, Is.True);
            Assert.That(flow.SupportsPdfInput, Is.True);
            Assert.That(flow.SupportsAudioInput, Is.False);
            Assert.That(flow.SupportsTools, Is.True);
        });

        var detailedOption = flow.Personalization.DetailOptions.First(o => o.Id == "detailed");
        flow.Personalization.SelectDetailCommand.Execute(detailedOption);
        Assert.That(flow.PreviewReplyKey.ToString(), Does.Contain(LocaleKey.Oobe_Preview_DetailedAddendum.I18N()));

        flow.AdvancedContextLimitText = "256000";
        flow.AdvancedOutputLimitText = "0";
        flow.SupportsVideoInput = true;
        flow.SupportsTools = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(advanced.ContextLimit, Is.EqualTo(256_000));
            Assert.That(flow.ContextLimitText, Is.EqualTo("256K"));
            Assert.That(advanced.OutputLimit, Is.Zero);
            Assert.That(flow.AdvancedOutputLimitText, Is.Null);
            Assert.That(flow.EndpointPreviewText, Is.EqualTo("https://models.contoso.dev/v1/chat/completions"));
            Assert.That(flow.AdvancedEndpointPlaceholder, Is.EqualTo("https://api.openai.com/v1"));
            Assert.That(advanced.InputModalities.HasFlag(Modalities.Video), Is.True);
            Assert.That(advanced.SupportsToolCall, Is.False);
        });

        advanced.Schema = ModelProviderSchema.Google;
        Dispatcher.UIThread.RunJobs();
        Assert.That(flow.AdvancedEndpointPlaceholder, Is.EqualTo("https://generativelanguage.googleapis.com/v1beta"));
    }

    [AvaloniaTest]
    public async Task PresetAndAdvancedCardProjections_ResolveCatalogAndPreservePlaceholderSemantics()
    {
        var presetCatalog = new PresetModelCatalog(
            [
                new PresetModelProviderCatalog(
                    "openai",
                    ModelCatalogSnapshot<ModelDefinitionTemplate, string>.Create(
                    [
                        KeyValuePair.Create(
                            "gpt-5-mini",
                            new ModelDefinitionTemplate
                            {
                                ModelId = "gpt-5-mini",
                                Name = "GPT-5 Mini",
                                ContextLimit = 400_000,
                                OutputLimit = 128_000,
                                InputModalities = Modalities.Text | Modalities.Image,
                                OutputModalities = Modalities.Text,
                                SupportsToolCall = true,
                                IsDefault = true
                            }),
                        KeyValuePair.Create(
                            "gpt-5.4",
                            new ModelDefinitionTemplate
                            {
                                ModelId = "gpt-5.4",
                                Name = "GPT-5.4",
                                ContextLimit = 1_100_000,
                                OutputLimit = 128_000,
                                InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                                OutputModalities = Modalities.Text,
                                SupportsToolCall = true
                            })
                    ]),
                    PresetModelSourceStatus.Available,
                    new HashSet<string>(["gpt-5-mini", "gpt-5.4"]))
            ],
            isValidated: true);
        var presetProvider = Substitute.For<IPresetModelProvider>();
        presetProvider.Catalog.Returns(presetCatalog);
        var officialProvider = Substitute.For<IOfficialModelProvider>();
        officialProvider.Catalog.Returns(OfficialModelCatalog.Empty);
        var assistantCatalog = new AssistantCatalog(presetProvider, officialProvider);

        using var harness = new TestHarness();
        using var flow = new WelcomeFlow(
            harness.Settings,
            harness.CloudClient,
            harness.ModelSessionFactory,
            new PromptEditorViewModel(
                harness.PromptService,
                Substitute.For<ISkillPromptProvider>(),
                NullLogger<PromptEditorViewModel>.Instance),
            assistantCatalog,
            presetProvider,
            officialProvider);

        flow.SelectSourceCommand.Execute(WelcomeModelSource.Own);
        await flow.ContinueCommand.ExecuteAsync(null); // Intro -> Source
        await flow.ContinueCommand.ExecuteAsync(null); // Source -> Configure (Built-in Providers tab)

        Assert.That(flow.ShowTelemetryMetrics, Is.False);
        Assert.That(flow.CanSelectCatalogModel, Is.False);

        var openAiTemplate = WelcomeFlow.PresetProviders.First(p => p.Id == "openai");
        flow.SelectedPresetProvider = openAiTemplate;
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(flow.CanSelectCatalogModel, Is.True);
            Assert.That(flow.CatalogModels, Has.Count.EqualTo(2));
            Assert.That(flow.SelectedCatalogModel?.Model.ModelId, Is.EqualTo("gpt-5-mini"));
            Assert.That(flow.ShowTelemetryMetrics, Is.True);
            Assert.That(flow.ContextLimitText, Is.EqualTo("400K"));
        });

        flow.SelectedCatalogModel = flow.CatalogModels[1];
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(flow.Assistant.Configuration.ModelId, Is.EqualTo("gpt-5.4"));
            Assert.That(flow.ContextLimitText, Is.EqualTo("1.1M"));
            Assert.That(flow.SupportsPdfInput, Is.True);
        });
    }

    [AvaloniaTest]
    public async Task SelectedPresetProvider_WhenCatalogHasNewerRelease_SelectsNewerModelOverOlderDefault()
    {
        var presetCatalog = new PresetModelCatalog(
            [
                new PresetModelProviderCatalog(
                    "openai",
                    ModelCatalogSnapshot<ModelDefinitionTemplate, string>.Create(
                    [
                        KeyValuePair.Create(
                            "gpt-5.6",
                            new ModelDefinitionTemplate
                            {
                                ModelId = "gpt-5.6",
                                Name = "GPT-5.6",
                                ContextLimit = 1_100_000,
                                OutputLimit = 128_000,
                                InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                                OutputModalities = Modalities.Text,
                                SupportsToolCall = true,
                                ReleaseDate = new DateOnly(2026, 10, 1)
                            }),
                        KeyValuePair.Create(
                            "gpt-5.4",
                            new ModelDefinitionTemplate
                            {
                                ModelId = "gpt-5.4",
                                Name = "GPT-5.4",
                                ContextLimit = 1_050_000,
                                OutputLimit = 128_000,
                                InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                                OutputModalities = Modalities.Text,
                                SupportsToolCall = true,
                                ReleaseDate = new DateOnly(2026, 3, 1),
                                IsDefault = true
                            })
                    ]),
                    PresetModelSourceStatus.Available,
                    new HashSet<string>(["gpt-5.6", "gpt-5.4"]))
            ],
            isValidated: true);
        var presetProvider = Substitute.For<IPresetModelProvider>();
        presetProvider.Catalog.Returns(presetCatalog);
        var officialProvider = Substitute.For<IOfficialModelProvider>();
        officialProvider.Catalog.Returns(OfficialModelCatalog.Empty);
        var assistantCatalog = new AssistantCatalog(presetProvider, officialProvider);

        using var harness = new TestHarness();
        using var flow = new WelcomeFlow(
            harness.Settings,
            harness.CloudClient,
            harness.ModelSessionFactory,
            new PromptEditorViewModel(
                harness.PromptService,
                Substitute.For<ISkillPromptProvider>(),
                NullLogger<PromptEditorViewModel>.Instance),
            assistantCatalog,
            presetProvider,
            officialProvider);

        flow.SelectSourceCommand.Execute(WelcomeModelSource.Own);
        await flow.ContinueCommand.ExecuteAsync(null);
        await flow.ContinueCommand.ExecuteAsync(null);

        flow.SelectedPresetProvider = WelcomeFlow.PresetProviders.First(p => p.Id == "openai");
        Dispatcher.UIThread.RunJobs();

        Assert.That(flow.SelectedCatalogModel?.Model.ModelId, Is.EqualTo("gpt-5.6"));
        Assert.That(flow.Assistant.Configuration.ModelId, Is.EqualTo("gpt-5.6"));
    }

    private sealed class TestHarness : IDisposable
    {
        public ServiceProvider Services { get; }
        public Settings Settings { get; }
        public ICloudClient CloudClient { get; } = Substitute.For<ICloudClient>();
        public IModelSessionFactory ModelSessionFactory { get; } = Substitute.For<IModelSessionFactory>();
        public IPromptService PromptService { get; } = Substitute.For<IPromptService>();

        public TestHarness()
        {
            Services = new ServiceCollection().BuildServiceProvider();
            Settings = new Settings(Services) { CompletedOobeVersion = 0 };
            Settings.Shortcut.ChatWindow.IsEnabled = true;
            Settings.Shortcut.ChatWindow.Main = new KeyboardShortcut(Key.Space, KeyModifiers.Alt);

            CloudClient.UserProfile.Returns((UserProfile?)null);
            var chatClient = Substitute.For<MEAI.IChatClient>();
            chatClient.GetStreamingResponseAsync(
                    Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                    Arg.Any<MEAI.ChatOptions?>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ => StreamOkAsync());
            ModelSessionFactory.Create(Arg.Any<Assistant>()).Returns(_ => new FakeModelSession(chatClient));

            PromptService.CreatePromptAsync(Arg.Any<PromptCreateRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<PromptCreateRequest>();
                    return new PromptDefinition(
                        request.Id ?? Guid.CreateVersion7(),
                        request.Name,
                        request.Template,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        request.Source,
                        request.MetadataPayload?.ToArray());
                });
            PromptService.UpdatePromptAsync(Arg.Any<Guid>(), Arg.Any<PromptUpdateRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var id = call.Arg<Guid>();
                    var request = call.Arg<PromptUpdateRequest>();
                    return new PromptDefinition(
                        id,
                        request.Name,
                        request.Template,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        request.Source ?? PromptSource.Guided,
                        request.MetadataPayload?.ToArray());
                });
        }

        public WelcomeFlow CreateFlow()
        {
            var promptEditor = new PromptEditorViewModel(
                PromptService,
                Substitute.For<ISkillPromptProvider>(),
                NullLogger<PromptEditorViewModel>.Instance);
            return new WelcomeFlow(Settings, CloudClient, ModelSessionFactory, promptEditor);
        }

        public void SetSignedIn(UserProfile profile)
        {
            CloudClient.UserProfile.Returns(profile);
            CloudClient.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(
                CloudClient,
                new PropertyChangedEventArgs(nameof(ICloudClient.UserProfile)));
        }

        public void Dispose() => Services.Dispose();

        private static async IAsyncEnumerable<MEAI.ChatResponseUpdate> StreamOkAsync()
        {
            await Task.CompletedTask;
            yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "ok");
        }

        private sealed class FakeModelSession(MEAI.IChatClient chatClient)
            : ModelSession(
                new AdvancedAssistantConfiguration(),
                new ModelConnection(ModelProviderSchema.OpenAI, "https://example.invalid", null, new HttpClient(), null))
        {
            public override MEAI.IChatClient ChatClient => chatClient;
            public override string Dialect => "openai";
        }
    }

    [AvaloniaTest]
    public async Task ShortcutStep_WhenSkippedViaSecondaryAction_DisablesShortcutAndCompletesFlow()
    {
        using var harness = new TestHarness();
        using var flow = harness.CreateFlow();

        await flow.ContinueCommand.ExecuteAsync(null); // -> Source
        flow.SelectSourceCommand.Execute(WelcomeModelSource.Own);
        flow.SelectModeCommand.Execute(true);
        await flow.ContinueCommand.ExecuteAsync(null); // -> Configure
        var advanced = (AdvancedAssistantConfiguration)flow.Assistant.Configuration;
        advanced.Endpoint = "https://api.example.com/v1";
        advanced.Schema = ModelProviderSchema.OpenAI;
        advanced.ModelId = "gpt-5-mini";
        await flow.ContinueCommand.ExecuteAsync(null); // Test connectivity
        await flow.ContinueCommand.ExecuteAsync(null); // -> Personalize
        flow.Personalization.ToggleScenarioCommand.Execute(flow.Personalization.ScenarioOptions[0]);
        await flow.ContinueCommand.ExecuteAsync(null); // -> Shortcut

        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeShortcutStep>());
        harness.Settings.Shortcut.ChatWindow.Main = default;
        Assert.That(flow.CanContinue, Is.False);
        Assert.That(flow.CanSecondaryAction, Is.True);

        await flow.SecondaryActionCommand.ExecuteAsync(null);
        Assert.Multiple(() =>
        {
            Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeTextSelectionStep>());
            Assert.That(harness.Settings.Shortcut.ChatWindow.IsEnabled, Is.False);
            Assert.That(harness.Settings.CompletedOobeVersion, Is.Zero);
        });

        await flow.SecondaryActionCommand.ExecuteAsync(null);
        Assert.Multiple(() =>
        {
            Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeFinishStep>());
            Assert.That(harness.Settings.TextSelection.IsToolbarEnabled, Is.False);
            Assert.That(harness.Settings.CompletedOobeVersion, Is.EqualTo(OobeCoordinator.CurrentVersion));
            Assert.That(flow.HasPreviousStep, Is.True);
        });
    }

    [AvaloniaTest]
    public async Task FinishStep_SelectFontSizeAndBackNavigation_MapsStopsOneTwoFourAndReturnsToTextSelection()
    {
        using var harness = new TestHarness();
        using var flow = harness.CreateFlow();

        await flow.ContinueCommand.ExecuteAsync(null); // -> Source
        flow.SelectSourceCommand.Execute(WelcomeModelSource.Own);
        flow.SelectModeCommand.Execute(true);
        await flow.ContinueCommand.ExecuteAsync(null); // -> Configure
        var advanced = (AdvancedAssistantConfiguration)flow.Assistant.Configuration;
        advanced.Endpoint = "https://api.example.com/v1";
        advanced.Schema = ModelProviderSchema.OpenAI;
        advanced.ModelId = "gpt-5-mini";
        await flow.ContinueCommand.ExecuteAsync(null); // Test connectivity
        await flow.ContinueCommand.ExecuteAsync(null); // -> Personalize
        flow.Personalization.ToggleScenarioCommand.Execute(flow.Personalization.ScenarioOptions[0]);
        await flow.ContinueCommand.ExecuteAsync(null); // -> Shortcut
        await flow.ContinueCommand.ExecuteAsync(null); // -> TextSelection
        await flow.ContinueCommand.ExecuteAsync(null); // -> Finish

        Assert.Multiple(() =>
        {
            Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeFinishStep>());
            Assert.That(flow.HasPreviousStep, Is.True);
            Assert.That(flow.CanMovePrevious, Is.True);
            Assert.That(harness.Settings.Display.FontSize, Is.Zero);
            Assert.That(flow.IsFontSizeSmall, Is.False);
            Assert.That(flow.IsFontSizeMedium, Is.True);
            Assert.That(flow.IsFontSizeLarge, Is.False);
        });

        flow.SelectFontSizeCommand.Execute(-1);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Settings.Display.FontSize, Is.EqualTo(-1));
            Assert.That(flow.IsFontSizeSmall, Is.True);
            Assert.That(flow.IsFontSizeMedium, Is.False);
            Assert.That(flow.IsFontSizeLarge, Is.False);
        });

        flow.SelectFontSizeCommand.Execute(2);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Settings.Display.FontSize, Is.EqualTo(2));
            Assert.That(flow.IsFontSizeSmall, Is.False);
            Assert.That(flow.IsFontSizeMedium, Is.False);
            Assert.That(flow.IsFontSizeLarge, Is.True);
        });

        flow.SelectFontSizeCommand.Execute(0);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Settings.Display.FontSize, Is.Zero);
            Assert.That(flow.IsFontSizeSmall, Is.False);
            Assert.That(flow.IsFontSizeMedium, Is.True);
            Assert.That(flow.IsFontSizeLarge, Is.False);
        });

        flow.MovePreviousCommand.Execute(null);
        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeTextSelectionStep>());

        await flow.ContinueCommand.ExecuteAsync(null);
        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeFinishStep>());
    }

    [AvaloniaTest]
    public async Task PersonalizationStep_WhenNavigatingBackAndForth_UpdatesPromptInPlaceWithoutDuplicatingAssistant()
    {
        using var harness = new TestHarness();
        using var flow = harness.CreateFlow();

        await flow.ContinueCommand.ExecuteAsync(null); // -> Source
        flow.SelectSourceCommand.Execute(WelcomeModelSource.Own);
        flow.SelectModeCommand.Execute(true);
        await flow.ContinueCommand.ExecuteAsync(null); // -> Configure
        var advanced = (AdvancedAssistantConfiguration)flow.Assistant.Configuration;
        advanced.Endpoint = "https://api.example.com/v1";
        advanced.Schema = ModelProviderSchema.OpenAI;
        advanced.ModelId = "gpt-5-mini";
        await flow.ContinueCommand.ExecuteAsync(null); // Check connectivity
        await flow.ContinueCommand.ExecuteAsync(null); // -> Personalize
        flow.Personalization.ToggleScenarioCommand.Execute(flow.Personalization.ScenarioOptions[0]);

        await flow.ContinueCommand.ExecuteAsync(null); // Save #1 -> Shortcut
        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeShortcutStep>());
        var promptId = flow.Assistant.SystemPromptId;

        flow.MovePreviousCommand.Execute(null); // Back -> Personalize
        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomePersonalizationStep>());
        await flow.ContinueCommand.ExecuteAsync(null); // Save #2 -> Shortcut
        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeShortcutStep>());

        Assert.Multiple(() =>
        {
            Assert.That(harness.Settings.Model.CustomAssistants, Has.Count.EqualTo(1));
            Assert.That(flow.Assistant.SystemPromptId, Is.EqualTo(promptId));
        });
        await harness.PromptService.Received(1).CreatePromptAsync(Arg.Any<PromptCreateRequest>(), Arg.Any<CancellationToken>());
        await harness.PromptService.Received(1).UpdatePromptAsync(promptId, Arg.Any<PromptUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [AvaloniaTest]
    public async Task MergedOwnConfigure_TabSwitchingPreservesDraftsValidatesActiveTabAndSupportsBackAndCloudSwitch()
    {
        using var harness = new TestHarness();
        using var flow = harness.CreateFlow();

        await flow.ContinueCommand.ExecuteAsync(null); // Intro -> Source
        Assert.That(flow.Progress, Has.Count.EqualTo(8));
        flow.SelectSourceCommand.Execute(WelcomeModelSource.Own);
        Assert.That(flow.Progress, Has.Count.EqualTo(7));
        await flow.ContinueCommand.ExecuteAsync(null); // Source -> Configure (Built-in Providers)

        Assert.Multiple(() =>
        {
            Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeConfigurationStep>());
            Assert.That(flow.IsPresetMode, Is.True);
            Assert.That(flow.CurrentStep?.TitleKey.ToString(), Is.EqualTo(LocaleKey.Oobe_Configure_OwnTitle.I18N()));
            Assert.That(flow.Progress, Has.Count.EqualTo(7));
            Assert.That(flow.Progress.Single(static item => item.IsCurrent).Stage, Is.EqualTo(3));
        });

        // Fill Tab 1 (Built-in Providers), switch to Tab 2 (Custom Protocol), fill invalid/partial then valid, switch back -> Tab 1 intact
        var preset = (PresetAssistantConfiguration)flow.Assistant.Configuration;
        preset.ProviderId = "openai";
        preset.ModelId = "gpt-5-mini";

        flow.SelectModeCommand.Execute(true);
        Assert.That(flow.IsAdvancedOwnMode, Is.True);
        var advanced = (AdvancedAssistantConfiguration)flow.Assistant.Configuration;
        advanced.Endpoint = "https://custom.local/v1";
        advanced.Schema = ModelProviderSchema.OpenAI;
        advanced.ModelId = string.Empty; // Invalid in Tab 2 right now

        // Submitting in Tab 2 validates ONLY Tab 2 (ignores valid Tab 1 draft)
        await flow.ContinueCommand.ExecuteAsync(null);
        Assert.That(flow.IsConnectivityChecked, Is.False);
        Assert.That(flow.ErrorKey?.ToString(), Is.EqualTo(LocaleKey.Oobe_InvalidConfiguration.I18N()));

        // Fill Tab 2 model ID and switch back to Tab 1 -> both drafts remain intact
        advanced.ModelId = "qwen3-coder";
        flow.SelectModeCommand.Execute(false);
        Assert.Multiple(() =>
        {
            Assert.That(flow.ErrorKey, Is.Null);
            Assert.That(flow.Assistant.Configuration, Is.SameAs(preset));
            Assert.That(preset.ProviderId, Is.EqualTo("openai"));
            Assert.That(preset.ModelId, Is.EqualTo("gpt-5-mini"));
        });

        flow.SelectModeCommand.Execute(true);
        Assert.Multiple(() =>
        {
            Assert.That(flow.Assistant.Configuration, Is.SameAs(advanced));
            Assert.That(advanced.Endpoint, Is.EqualTo("https://custom.local/v1"));
            Assert.That(advanced.ModelId, Is.EqualTo("qwen3-coder"));
        });

        // Back from merged Configure returns directly to Source with aligned 7-step progress
        flow.MovePreviousCommand.Execute(null);
        Assert.Multiple(() =>
        {
            Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeSourceStep>());
            Assert.That(flow.Progress, Has.Count.EqualTo(7));
            Assert.That(flow.Progress.Single(static item => item.IsCurrent).Stage, Is.EqualTo(1));
        });

        // Re-enter merged Configure and click bottom Cloud fallback link -> jumps to Cloud Login & restores 8-step Cloud progress
        await flow.ContinueCommand.ExecuteAsync(null);
        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeConfigurationStep>());
        flow.SwitchToCloudCommand.Execute(null);
        Assert.Multiple(() =>
        {
            Assert.That(flow.SelectedSource, Is.EqualTo(WelcomeModelSource.Cloud));
            Assert.That(flow.IsCloudSource, Is.True);
            Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeLoginStep>());
            Assert.That(flow.Progress, Has.Count.EqualTo(8));
            Assert.That(flow.Progress.Select(static item => item.Stage), Is.EqualTo([0, 1, 2, 3, 4, 5, 6, 7]));
            Assert.That(flow.Progress.Single(static item => item.IsCurrent).Stage, Is.EqualTo(2));
            Assert.That(flow.Progress.Where(static item => item.IsReached).Select(static item => item.Stage), Is.EqualTo([0, 1, 2]));
        });

        // Back from Cloud Login returns cleanly to Source
        flow.MovePreviousCommand.Execute(null);
        Assert.That(flow.CurrentStep, Is.TypeOf<WelcomeSourceStep>());
    }

    [AvaloniaTest]
    public void SelectedPresetProvider_WhenProviderChanges_ClearsStoredApiKeySelection()
    {
        using var harness = new TestHarness();
        using var flow = harness.CreateFlow();
        var apiKey = new ApiKey { Name = "OpenAI Key", SecretKey = "secret-openai" };
        harness.Settings.Model.ApiKeys.Add(apiKey);

        flow.SelectSourceCommand.Execute(WelcomeModelSource.Own);
        flow.SelectedPresetProvider = WelcomeFlow.PresetProviders.First(p => p.Id == "openai");
        flow.SelectedApiKey = apiKey;
        Dispatcher.UIThread.RunJobs();

        Assert.That(flow.PresetConfiguration?.ApiKey, Is.EqualTo(apiKey.Id));
        Assert.That(flow.SelectedApiKey, Is.SameAs(apiKey));

        flow.SelectedPresetProvider = WelcomeFlow.PresetProviders.First(p => p.Id == "anthropic");
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(flow.PresetConfiguration?.ProviderId, Is.EqualTo("anthropic"));
            Assert.That(flow.PresetConfiguration?.ApiKey, Is.EqualTo(Guid.Empty));
            Assert.That(flow.SelectedApiKey, Is.SameAs(ApiKey.Empty));
        });
    }

    private sealed class MessageRecorder<T>(Action onReceive) : IRecipient<T> where T : class
    {
        public void Receive(T message) => onReceive();
    }
}
