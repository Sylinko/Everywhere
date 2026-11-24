using Avalonia.Controls.Primitives;

namespace Everywhere.ViewModels;

/// <summary>
/// Shared context for the full welcome flow's typed decision and stage templates.
/// </summary>
public abstract class WelcomeStep(WelcomeFlow flow, int stage, string stageKey, string title, string? description)
    : OobeStep(stage, stageKey, title, description)
{
    public WelcomeFlow Flow { get; } = flow;
}

/// <summary>
/// Introduces the assistant before asking for configuration choices.
/// </summary>
public sealed class WelcomeIntroStep(WelcomeFlow flow)
    : WelcomeStep(flow, 0, "intro", LocaleKey.Oobe_Intro_Title, LocaleKey.Oobe_Intro_Description)
{
    public override IDynamicLocaleKey PrimaryLabelKey => new DynamicLocaleKey(LocaleKey.Oobe_Start);
}

/// <summary>
/// Requires an explicit, equally weighted cloud or own-model choice.
/// </summary>
public sealed class WelcomeSourceStep(WelcomeFlow flow)
    : WelcomeStep(flow, 1, "source", LocaleKey.Oobe_Source_Title, LocaleKey.Oobe_Source_Description)
{
    public override bool CanContinue => Flow.SelectedSource is not null;
}

/// <summary>
/// Applies only to the unsigned-in cloud route; triggers browser authentication before advancing.
/// </summary>
public sealed class WelcomeLoginStep(WelcomeFlow flow)
    : WelcomeStep(flow, 2, "login", LocaleKey.Oobe_Login_Title, LocaleKey.Oobe_Login_Description)
{
    public override bool IsApplicable => Flow is { IsCloudSource: true, IsSignedIn: false };
    public override bool IsIncludedInProgress => !Flow.IsOwnSource && (!Flow.IsSignedIn || Flow.IsLoginStepInProgressPath(this));
    public override bool CanContinue => Flow.IsSignedIn || Flow.CanLogIn;

    public override IDynamicLocaleKey PrimaryLabelKey =>
        new DynamicLocaleKey(Flow.IsSignedIn ? LocaleKey.Oobe_Continue : LocaleKey.Oobe_Login_SignIn);

    public override async Task<bool> ContinueAsync(CancellationToken cancellationToken)
    {
        if (Flow.IsSignedIn) return true;
        await Flow.AuthenticateCloudAsync(cancellationToken);
        return false;
    }
}

/// <summary>
/// Validates and tests configuration before the assistant is saved or the user advances.
/// </summary>
public sealed class WelcomeConfigurationStep(WelcomeFlow flow)
    : WelcomeStep(flow, 3, "configure", LocaleKey.Oobe_Configure_Title, null)
{
    public override IDynamicLocaleKey TitleKey =>
        new DynamicLocaleKey(Flow.IsCloudSource ? LocaleKey.Oobe_Configure_Title : LocaleKey.Oobe_Configure_OwnTitle);

    public override IDynamicLocaleKey? DescriptionKey =>
        Flow.IsCloudSource ? new DynamicLocaleKey(LocaleKey.Oobe_Configure_Description) : null;

    public override IDynamicLocaleKey PrimaryLabelKey =>
        new DynamicLocaleKey(Flow.IsConnectivityChecked ? LocaleKey.Oobe_Continue : LocaleKey.Oobe_TestConnection);

    public override Task<bool> ContinueAsync(CancellationToken cancellationToken) => Flow.CheckConnectivityAsync(cancellationToken);
}

/// <summary>
/// Creates a normal guided prompt and assigns it to this run's assistant.
/// </summary>
public sealed class WelcomePersonalizationStep(WelcomeFlow flow)
    : WelcomeStep(flow, 4, "personalize", LocaleKey.Oobe_Personalize_Title, LocaleKey.Oobe_Personalize_Description)
{
    public override bool IsSplitLayout => true;
    public override bool CanContinue => Flow is { HasScenarios: true, IsConnectivityChecked: true };
    public override ScrollBarVisibility DecisionScrollBarVisibility => ScrollBarVisibility.Disabled;
    public override IDynamicLocaleKey PrimaryLabelKey => new DynamicLocaleKey(LocaleKey.Oobe_SavePreferences);
    public override Task<bool> ContinueAsync(CancellationToken cancellationToken) => Flow.SavePersonalizationAsync(cancellationToken);
}

/// <summary>
/// Confirms a valid shortcut to enable it, or skips to disable the global wake-up shortcut.
/// </summary>
public sealed class WelcomeShortcutStep(WelcomeFlow flow)
    : WelcomeStep(flow, 5, "shortcut", LocaleKey.Oobe_Shortcut_Title, LocaleKey.Oobe_Shortcut_Description)
{
    public override bool CanContinue =>
        Flow is { HasValidShortcut: true, IsConnectivityChecked: true } && Flow.Assistant.SystemPromptId != Guid.Empty;

    public override IDynamicLocaleKey PrimaryLabelKey => new DynamicLocaleKey(LocaleKey.Oobe_Next);

    public override IDynamicLocaleKey SecondaryActionLabelKey { get; } = new DynamicLocaleKey(LocaleKey.Oobe_Shortcut_Skip);

    public override bool CanExecuteSecondaryAction =>
        Flow.IsConnectivityChecked && Flow.Assistant.SystemPromptId != Guid.Empty;

    public override Task<bool> ContinueAsync(CancellationToken cancellationToken) => Task.FromResult(Flow.ConfirmShortcut());

    public override Task<bool> SecondaryActionAsync(CancellationToken cancellationToken) => Task.FromResult(Flow.SkipShortcut());
}

/// <summary>
/// Demonstrates the floating text-selection toolbar and lets the user enable or disable it.
/// </summary>
public sealed class WelcomeTextSelectionStep(WelcomeFlow flow)
    : WelcomeStep(flow, 6, "text-selection", LocaleKey.Oobe_TextSelection_Title, LocaleKey.Oobe_TextSelection_Description)
{
    public override bool CanContinue => Flow.IsConnectivityChecked && Flow.Assistant.SystemPromptId != Guid.Empty;

    public override IDynamicLocaleKey PrimaryLabelKey => new DynamicLocaleKey(LocaleKey.Oobe_Next);

    public override IDynamicLocaleKey SecondaryActionLabelKey { get; } = new DynamicLocaleKey(LocaleKey.Oobe_TextSelection_Skip);

    public override bool CanExecuteSecondaryAction =>
        Flow.IsConnectivityChecked && Flow.Assistant.SystemPromptId != Guid.Empty;

    public override Task<bool> ContinueAsync(CancellationToken cancellationToken) =>
        Flow.ConfirmTextSelectionAsync(cancellationToken);

    public override Task<bool> SecondaryActionAsync(CancellationToken cancellationToken) =>
        Flow.SkipTextSelectionAsync(cancellationToken);
}

/// <summary>
/// Shows already-recorded completion and the optional diagnostic preference.
/// </summary>
public sealed class WelcomeFinishStep(WelcomeFlow flow)
    : WelcomeStep(flow, 7, "finish", LocaleKey.Oobe_Finish_Title, LocaleKey.Oobe_Finish_Description)
{
    public override bool IsFinal => true;
    public override bool CanMovePrevious => true;
    public override IDynamicLocaleKey PrimaryLabelKey => new DynamicLocaleKey(LocaleKey.Oobe_StartUsing);
}
