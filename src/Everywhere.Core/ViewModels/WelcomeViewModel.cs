using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.AI.Configurator;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.ViewModels;

public sealed partial class WelcomeViewModel : BusyViewModelBase
{
    [ObservableProperty]
    public partial WelcomeViewModelStep? CurrentStep { get; private set; }

    [ObservableProperty]
    public partial bool IsPageTransitionReversed { get; private set; }

    /// <summary>
    /// Only allow skipping if there are more steps ahead.
    /// </summary>
    public bool CanSkipAll => _currentStepIndex < _steps.Count - 1;

    /// <summary>
    /// Only allow moving next if there are more steps ahead.
    /// </summary>
    public bool CanMoveNext => _currentStepIndex < _steps.Count - 1;

    /// <summary>
    /// Only allow moving previous if there are steps behind.
    /// </summary>
    public bool CanMovePrevious => _currentStepIndex > 0;

    public Settings Settings { get; }

    public CustomAssistant Assistant { get; }

    public ICloudClient CloudClient { get; }

    [ObservableProperty]
    public partial bool IsConnectivityChecked { get; set; }


    private readonly IReadOnlyList<WelcomeViewModelStep> _steps;
    private int _currentStepIndex;

    public WelcomeViewModel(IServiceProvider serviceProvider)
    {
        Settings = serviceProvider.GetRequiredService<Settings>();
        CloudClient = serviceProvider.GetRequiredService<ICloudClient>();

        // Initialize a default custom assistant
        Assistant = new CustomAssistant
        {
            Name = LocaleResolver.CustomAssistant_Name_Default,
            ConfiguratorType = AssistantConfiguratorType.Official
        };
        Assistant.PropertyChanged += delegate
        {
            // Reset connectivity check when assistant configuration changes
            if (IsNotBusy) IsConnectivityChecked = false;
        };

        _steps =
        [
            new WelcomeViewModelIntroStep(this),
            new WelcomeViewModelSoftLoginStep(this),
            new WelcomeViewModelConfiguratorStep(this),
            new WelcomeViewModelHardLoginStep(this),
            new WelcomeViewModelAssistantStep(this, serviceProvider),
            new WelcomeViewModelShortcutStep(this),
            new WelcomeViewModelTelemetryStep(this)
        ];

        CurrentStep = _steps[0];
    }

    [RelayCommand(CanExecute = nameof(CanMoveNext))]
    private void MoveNext()
    {
        IsPageTransitionReversed = false;
        _currentStepIndex++;
        CurrentStep = _steps[_currentStepIndex];
    }

    [RelayCommand(CanExecute = nameof(CanMovePrevious))]
    private void MovePrevious()
    {
        IsPageTransitionReversed = true;
        _currentStepIndex--;
        CurrentStep = _steps[_currentStepIndex];
    }

    public void MoveTo<TStep>() where TStep : WelcomeViewModelStep
    {
        var targetIndex = _steps.FindIndexOf(step => step is TStep);
        if (targetIndex == -1) return;

        IsPageTransitionReversed = targetIndex < _currentStepIndex;
        _currentStepIndex = targetIndex;
        CurrentStep = _steps[_currentStepIndex];
    }

    [RelayCommand]
    public void Close()
    {
        if (IsConnectivityChecked)
        {
            // Save the configured assistant
            Settings.Model.CustomAssistants.Add(Assistant);
        }

        CurrentStep?.CancellationTokenSource.Cancel();
        DialogHost.CloseAll();
    }
}

/// <summary>
/// The base class for welcome view model steps.
/// All step must inherit from this class.
/// When switching steps, the View layer will select the appropriate DataTemplate based on the step type.
/// </summary>
/// <param name="viewModel"></param>
public abstract class WelcomeViewModelStep(WelcomeViewModel viewModel) : BusyViewModelBase
{
    public WelcomeViewModel ViewModel { get; } = viewModel;

    public ReusableCancellationTokenSource CancellationTokenSource { get; } = new();
}

/// <summary>
/// The introduction step of the welcome view model.
/// </summary>
/// <param name="viewModel"></param>
public sealed partial class WelcomeViewModelIntroStep(WelcomeViewModel viewModel) : WelcomeViewModelStep(viewModel)
{
    [RelayCommand]
    private void MoveNext()
    {
        // Skip the soft login step if already logged in
        if (ViewModel.CloudClient.UserProfile is not null)
        {
            ViewModel.MoveTo<WelcomeViewModelConfiguratorStep>();
        }
        else
        {
            ViewModel.MoveTo<WelcomeViewModelSoftLoginStep>();
        }

    }
}

public sealed class WelcomeViewModelSoftLoginStep(WelcomeViewModel viewModel) : WelcomeViewModelStep(viewModel);

public sealed partial class WelcomeViewModelConfiguratorStep(WelcomeViewModel viewModel) : WelcomeViewModelStep(viewModel)
{
    [RelayCommand]
    private void MovePrevious()
    {
        // Skip the soft login step if already logged in
        if (ViewModel.CloudClient.UserProfile is not null)
        {
            ViewModel.MoveTo<WelcomeViewModelIntroStep>();
        }
        else
        {
            ViewModel.MoveTo<WelcomeViewModelSoftLoginStep>();
        }
    }

    [RelayCommand]
    private void MoveNext()
    {
        // Skip the hard login step if not using official configurator or already logged in
        if (ViewModel.Assistant.ConfiguratorType != AssistantConfiguratorType.Official || ViewModel.CloudClient.UserProfile is not null)
        {
            ViewModel.MoveTo<WelcomeViewModelAssistantStep>();
        }
        else
        {
            ViewModel.MoveTo<WelcomeViewModelHardLoginStep>();
        }
    }
}

public sealed class WelcomeViewModelHardLoginStep(WelcomeViewModel viewModel) : WelcomeViewModelStep(viewModel);

/// <summary>
/// Message to trigger confetti effect in the UI.
/// </summary>
public sealed record ShowConfettiEffectMessage;

public sealed partial class WelcomeViewModelAssistantStep(WelcomeViewModel viewModel, IServiceProvider serviceProvider)
    : WelcomeViewModelStep(viewModel)
{
    private readonly IKernelMixinFactory _kernelMixinFactory = serviceProvider.GetRequiredService<IKernelMixinFactory>();
    private readonly ILogger<WelcomeViewModelAssistantStep> _logger = serviceProvider.GetRequiredService<ILogger<WelcomeViewModelAssistantStep>>();

    [RelayCommand]
    private void MovePrevious()
    {
        ViewModel.MoveTo<WelcomeViewModelConfiguratorStep>();
    }

    [RelayCommand]
    private Task CheckConnectivityAsync()
    {
        ViewModel.IsConnectivityChecked = false;
        if (!ViewModel.Assistant.Configurator.Validate()) return Task.CompletedTask;

        return ExecuteBusyTaskAsync(
            async cancellationToken =>
            {
                KernelMixin? kernelMixin = null;
                try
                {
                    kernelMixin = _kernelMixinFactory.Create(ViewModel.Assistant);
                    await kernelMixin.CheckConnectivityAsync(cancellationToken);
                    StrongReferenceMessenger.Default.Send(new ShowConfettiEffectMessage());
                    ViewModel.IsConnectivityChecked = true;
                }
                catch (Exception ex)
                {
                    ex = HandledChatException.Handle(ex, kernelMixin);
                    _logger.LogError(ex, "Failed to validate assistant connectivity");
                    ToastHost
                        .CreateToast(LocaleResolver.WelcomeViewModel_ValidateApiKey_FailedToast_Title)
                        .WithContent(ex.GetFriendlyMessage().ToTextBlock())
                        .DismissOnClick()
                        .ShowError();
                }
                finally
                {
                    kernelMixin?.Dispose();
                }
            },
            cancellationToken: CancellationTokenSource.Token);
    }
}

public sealed class WelcomeViewModelShortcutStep(WelcomeViewModel viewModel) : WelcomeViewModelStep(viewModel);

public sealed partial class WelcomeViewModelTelemetryStep(WelcomeViewModel viewModel) : WelcomeViewModelStep(viewModel)
{
    [RelayCommand]
    private void SendOnlyNecessaryTelemetry()
    {
        ViewModel.Settings.Common.DiagnosticData = false;
        ViewModel.Close();
    }
}