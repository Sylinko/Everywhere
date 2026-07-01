using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using ShadUI;

namespace Everywhere.Views;

public sealed partial class ConsentDecisionCard : Card
{
    /// <summary>
    /// Defines the <see cref="RememberMasks"/> property.
    /// </summary>
    public static readonly StyledProperty<RequestConsentRememberMasks> RememberMasksProperty =
        AvaloniaProperty.Register<ConsentDecisionCard, RequestConsentRememberMasks>(nameof(RememberMasks), RequestConsentRememberMasks.All);

    /// <summary>
    /// Gets or sets a value indicating whether the user can choose to remember their decision.
    /// </summary>
    public RequestConsentRememberMasks RememberMasks
    {
        get => GetValue(RememberMasksProperty);
        set => SetValue(RememberMasksProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="CanDenyWithReason"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> CanDenyWithReasonProperty =
        AvaloniaProperty.Register<ConsentDecisionCard, bool>(nameof(CanDenyWithReason), true);

    /// <summary>
    /// Gets or sets a value indicating whether the user can choose to deny with a reason.
    /// </summary>
    public bool CanDenyWithReason
    {
        get => GetValue(CanDenyWithReasonProperty);
        set => SetValue(CanDenyWithReasonProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="IsReasonInputVisible"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsReasonInputVisibleProperty =
        AvaloniaProperty.Register<ConsentDecisionCard, bool>(nameof(IsReasonInputVisible));

    /// <summary>
    /// Gets or sets a value indicating whether the reason input field is visible.
    /// </summary>
    public bool IsReasonInputVisible
    {
        get => GetValue(IsReasonInputVisibleProperty);
        set => SetValue(IsReasonInputVisibleProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="Reason"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> ReasonProperty =
        AvaloniaProperty.Register<ConsentDecisionCard, string?>(nameof(Reason));

    /// <summary>
    /// Gets or sets the reason for denying the permission request, if applicable.
    /// </summary>
    public string? Reason
    {
        get => GetValue(ReasonProperty);
        set => SetValue(ReasonProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="Command"/> property.
    /// </summary>
    public static readonly StyledProperty<IRelayCommand<ConsentDecisionResult>?> CommandProperty =
        AvaloniaProperty.Register<ConsentDecisionCard, IRelayCommand<ConsentDecisionResult>?>(nameof(Command));

    /// <summary>
    /// Gets or sets the command to execute when the user submits their consent decision. The command parameter will be a <see cref="ConsentDecisionResult"/> containing the user's decision and optional reason.
    /// </summary>
    public IRelayCommand<ConsentDecisionResult>? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public bool CanAllowSession => RememberMasks.HasFlag(RequestConsentRememberMasks.AllowSession);

    public bool CanAlwaysAllow => RememberMasks.HasFlag(RequestConsentRememberMasks.AlwaysAllow);

    public bool CanRemember => CanAllowSession | CanAlwaysAllow;

    [RelayCommand]
    private void Submit(ConsentDecision decision)
    {
        if (Command is { } command)
        {
            var result = new ConsentDecisionResult(decision, decision == ConsentDecision.Deny && CanDenyWithReason ? Reason : null);
            if (command.CanExecute(result)) command.Execute(result);
        }
    }

    [RelayCommand]
    private void SetIsReasonInputVisible(bool isVisible)
    {
        IsReasonInputVisible = isVisible && CanDenyWithReason;
    }
}