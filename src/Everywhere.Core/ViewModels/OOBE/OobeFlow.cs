using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Serilog;

namespace Everywhere.ViewModels;

/// <summary>
/// Owns one UI-thread-affine onboarding run. Views supply templates for steps; the flow owns
/// prerequisites, conditional routing, history, cancellation, and completion effects.
/// </summary>
public abstract partial class OobeFlow : BusyViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSplitLayout))]
    [NotifyPropertyChangedFor(nameof(CompanionLayoutKey))]
    [NotifyPropertyChangedFor(nameof(IsSuccessGlowActive))]
    public partial OobeStep? CurrentStep { get; private set; }

    [ObservableProperty]
    public partial bool IsPageTransitionReversed { get; private set; }

    [ObservableProperty]
    public partial IDynamicLocaleKey? ErrorKey { get; protected set; }

    [ObservableProperty]
    public partial IReadOnlyList<OobeProgressItem> Progress { get; private set; } = [];

    protected IReadOnlyList<OobeStep> Steps { get; private set; } = [];

    public bool CanContinue => !_isClosed && IsNotBusy && CurrentStep is { CanContinue: true };

    public bool CanSecondaryAction => !_isClosed && IsNotBusy && CurrentStep is { HasSecondaryAction: true, CanExecuteSecondaryAction: true };

    public bool HasPreviousStep => _history.Count > 0 && (CurrentStep?.CanMovePrevious ?? false);

    public bool CanMovePrevious => !_isClosed && IsNotBusy && HasPreviousStep;

    public bool IsSplitLayout => CurrentStep is { IsSplitLayout: true };

    public string CompanionLayoutKey =>
        IsSplitLayout ? "split" :
        CurrentStep is null || CurrentStep.Stage == 0 || CurrentStep.IsFinal ? "hero" :
        "choice";

    public virtual bool IsSuccessGlowActive => CurrentStep is { IsFinal: true };

    public bool ShouldStartUsing { get; private set; }

    /// <summary>
    /// Requests closing the host window. Native window closure also disposes this run.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Signals the host view to play a scoped celebration effect (such as confetti on connection success).
    /// </summary>
    public event EventHandler? CelebrationRequested;

    private readonly List<OobeStep> _history = [];
    private readonly CancellationTokenSource _cancellation = new();
    private bool _isClosed;
    private bool _isDisposed;

    /// <summary>
    /// Initializes the ordered steps once. Steps sharing a stage share one progress marker.
    /// </summary>
    protected void InitializeSteps(IReadOnlyList<OobeStep> steps)
    {
        if (steps.Count == 0)
        {
            throw new ArgumentException("An onboarding flow must define at least one step.", nameof(steps));
        }

        Steps = steps;
        CurrentStep = steps[0];
        RefreshNavigation();
    }

    /// <summary>
    /// Refreshes command gates after a prerequisite changes without replacing step objects.
    /// </summary>
    protected void RefreshNavigation()
    {
        RebuildProgressIfNeeded();
        CurrentStep?.Refresh();
        foreach (var item in Progress)
        {
            item.IsCurrent = item.Stage == CurrentStep?.Stage;
            item.IsReached = item.Stage <= CurrentStep?.Stage;
            if (item.IsCurrent && CurrentStep is { } step)
            {
                item.TitleKey = step.TitleKey;
            }
        }

        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(CanSecondaryAction));
        OnPropertyChanged(nameof(HasPreviousStep));
        OnPropertyChanged(nameof(CanMovePrevious));
        ContinueCommand.NotifyCanExecuteChanged();
        SecondaryActionCommand.NotifyCanExecuteChanged();
        MovePreviousCommand.NotifyCanExecuteChanged();
    }

    protected bool IsStepInProgressHistory(OobeStep step) =>
        ReferenceEquals(CurrentStep, step) || _history.Contains(step);

    protected void JumpToStep(OobeStep targetStep, IEnumerable<OobeStep> historySteps, bool isReversed)
    {
        _history.Clear();
        _history.AddRange(historySteps);
        IsPageTransitionReversed = isReversed;
        ErrorKey = null;
        CurrentStep = targetStep;
        RefreshNavigation();
    }

    private void RebuildProgressIfNeeded()
    {
        var expectedSteps = Steps
            .AsValueEnumerable()
            .Where(static step => step.IsIncludedInProgress)
            .DistinctBy(static step => step.Stage)
            .ToArray();

        if (Progress.Count == expectedSteps.Length &&
            Progress.AsValueEnumerable().Zip(expectedSteps).All(static pair => pair.First.Stage == pair.Second.Stage))
        {
            return;
        }

        Progress = expectedSteps
            .AsValueEnumerable()
            .Select(static step => new OobeProgressItem(step.Stage, step.TitleKey))
            .ToArray();
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnCurrentStepChanged(OobeStep? value) => OnCurrentStepChangedCore(value);

    /// <summary>
    /// Allows derived flows to update step-specific visual state when navigation changes.
    /// </summary>
    protected virtual void OnCurrentStepChangedCore(OobeStep? step) { }

    /// <summary>
    /// Triggers a scoped celebration signal for the attached host view.
    /// </summary>
    protected void RequestCelebration()
    {
        if (!_isClosed)
        {
            CelebrationRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Runs an operation owned by this window. Closing cancels work and prevents late navigation.
    /// </summary>
    protected Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (_isClosed || IsBusy) return Task.CompletedTask;
        return ExecuteBusyTaskAsync(
            async cancellationToken =>
            {
                ErrorKey = null;
                try
                {
                    await operation(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    if (_isClosed) return;
                    exception = HandledSystemException.Handle(exception);
                    Log.ForContext(GetType()).Error(exception, "Onboarding operation failed");
                    ErrorKey = exception.GetFriendlyMessage();
                }
            },
            cancellationToken: _cancellation.Token);
    }

    /// <summary>
    /// Runs the flow's first-use action after its window has closed and disposed the run.
    /// Implementations dispatch application actions and must not restart owned operations.
    /// </summary>
    public virtual void StartUsing() { }

    /// <inheritdoc />
    protected override void OnIsBusyChanged()
    {
        base.OnIsBusyChanged();
        RefreshNavigation();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            _isDisposed = true;
            _isClosed = true;
            _cancellation.Cancel();
            _cancellation.Dispose();
        }

        base.Dispose(disposing);
    }

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private Task ContinueAsync()
    {
        if (!CanContinue || CurrentStep is not { } step) return Task.CompletedTask;
        return ExecuteStepActionAsync(step, step.ContinueAsync);
    }

    [RelayCommand(CanExecute = nameof(CanSecondaryAction))]
    private Task SecondaryActionAsync()
    {
        if (!CanSecondaryAction || CurrentStep is not { } step) return Task.CompletedTask;
        return ExecuteStepActionAsync(step, step.SecondaryActionAsync);
    }

    private Task ExecuteStepActionAsync(OobeStep step, Func<CancellationToken, Task<bool>> action)
    {
        return RunAsync(async cancellationToken =>
        {
            if (!await action(cancellationToken)) return;
            cancellationToken.ThrowIfCancellationRequested();
            if (step.IsFinal)
            {
                ShouldStartUsing = true;
                Close();
                return;
            }

            var next = Steps
                .AsValueEnumerable()
                .SkipWhile(candidate => !ReferenceEquals(candidate, step))
                .Skip(1)
                .FirstOrDefault(static candidate => candidate.IsApplicable);
            if (next is null) return;

            _history.Add(step);
            IsPageTransitionReversed = false;
            CurrentStep = next;
            RefreshNavigation();
        });
    }

    [RelayCommand(CanExecute = nameof(CanMovePrevious))]
    private void MovePrevious()
    {
        if (!CanMovePrevious) return;
        IsPageTransitionReversed = true;
        CurrentStep = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        ErrorKey = null;
        RefreshNavigation();
    }

    [RelayCommand]
    private void Close()
    {
        if (_isClosed) return;
        _isClosed = true;
        _cancellation.Cancel();
        RefreshNavigation();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Lightweight, stable presentation and prerequisite contract for an onboarding step.
/// </summary>
public abstract class OobeStep(int stage, string stageKey, string title, string? description) : ObservableObject
{
    public int Stage { get; } = stage;

    /// <summary>
    /// Stable string key consumed by stage animations (e.g., <c>StateMotion</c>) without type inspection.
    /// </summary>
    public string StageKey { get; } = stageKey;

    public virtual IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(title);

    public virtual IDynamicLocaleKey? DescriptionKey { get; } =
        description is { Length: > 0 } ? new DynamicLocaleKey(description) : null;

    public virtual IDynamicLocaleKey PrimaryLabelKey => new DynamicLocaleKey(LocaleKey.Oobe_Continue);

    public virtual IDynamicLocaleKey? SecondaryActionLabelKey => null;

    public bool HasSecondaryAction => SecondaryActionLabelKey is not null;

    public virtual bool CanContinue => true;

    public virtual bool CanExecuteSecondaryAction => true;

    public virtual bool IsApplicable => true;

    /// <summary>
    /// Gets a value indicating whether this step's stage contributes a progress marker for the active route.
    /// </summary>
    public virtual bool IsIncludedInProgress => true;

    public virtual bool IsFinal => false;

    public virtual bool CanMovePrevious => !IsFinal;

    public virtual bool IsSplitLayout => false;

    /// <summary>
    /// Returns true only when the current step's required effects have succeeded.
    /// </summary>
    public virtual Task<bool> ContinueAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>
    /// Returns true only when the current step's secondary action (such as disabling an optional feature) has succeeded.
    /// </summary>
    public virtual Task<bool> SecondaryActionAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    internal void Refresh()
    {
        OnPropertyChanged(nameof(TitleKey));
        OnPropertyChanged(nameof(DescriptionKey));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(PrimaryLabelKey));
        OnPropertyChanged(nameof(CanExecuteSecondaryAction));
        OnPropertyChanged(nameof(SecondaryActionLabelKey));
        OnPropertyChanged(nameof(HasSecondaryAction));
    }
}

/// <summary>
/// One non-interactive progress marker. Markers never bypass step prerequisites.
/// </summary>
public sealed partial class OobeProgressItem(int stage, IDynamicLocaleKey titleKey) : ObservableObject
{
    public int Stage { get; } = stage;

    [ObservableProperty]
    public partial IDynamicLocaleKey TitleKey { get; set; } = titleKey;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool IsReached { get; set; }
}
