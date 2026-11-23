using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using ShadUI;

namespace Everywhere.Views;

/// <summary>
/// Unified WYSIWYG onboarding configuration card combining interactive provider, model, endpoint,
/// API key, token limit, capability, and live connectivity status telemetry in one surface.
/// </summary>
public sealed partial class OobeConfigureCard : UserControl
{
    /// <summary>
    /// Gets all API schemas exposed by the advanced configuration protocol pill selector.
    /// </summary>
    public static IReadOnlyList<ModelProviderSchema> AvailableSchemas { get; } = Enum.GetValues<ModelProviderSchema>();

    /// <summary>
    /// Defines the <see cref="Flow"/> property.
    /// </summary>
    public static readonly StyledProperty<WelcomeFlow?> FlowProperty =
        AvaloniaProperty.Register<OobeConfigureCard, WelcomeFlow?>(nameof(Flow));

    /// <summary>
    /// Gets or sets the onboarding flow driving the configuration card.
    /// </summary>
    public WelcomeFlow? Flow
    {
        get => GetValue(FlowProperty);
        set => SetValue(FlowProperty, value);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OobeConfigureCard"/> class.
    /// </summary>
    public OobeConfigureCard()
    {
        InitializeComponent();
        AdvancedContextLimitBox.AddHandler(TextInputEvent, TokenBox_OnTextInput, RoutingStrategies.Tunnel);
        AdvancedOutputLimitBox.AddHandler(TextInputEvent, TokenBox_OnTextInput, RoutingStrategies.Tunnel);
    }

    [RelayCommand]
    private async Task AddApiKeyAsync(CancellationToken cancellationToken)
    {
        if (Flow is not { } flow) return;
        InlineApiKeyComboBox.IsDropDownOpen = false;

        var defaultName = flow.DisplayProviderTemplate?.DisplayNameKey.ToString();
        var form = new CreateApiKeyForm(defaultName);
        var result = await DialogManager
            .CreateDialog(form, LocaleKey.ApiKeyComboBox_AddApiKey.I18N(), TopLevel.GetTopLevel(this))
            .WithPrimaryButton(
                LocaleKey.Common_OK.I18N(),
                (_, args) => args.Cancel = !form.ApiKey.ValidateAndSave())
            .WithCancelButton(LocaleKey.Common_Cancel.I18N())
            .ShowAsync(cancellationToken);
        if (result != DialogResult.Primary) return;

        flow.Settings.Model.ApiKeys.Add(form.ApiKey);
        flow.SelectedApiKey = form.ApiKey;
    }

    [RelayCommand]
    private async Task ManageApiKeyAsync(CancellationToken cancellationToken)
    {
        if (Flow is not { } flow) return;
        InlineApiKeyComboBox.IsDropDownOpen = false;

        var defaultName = flow.DisplayProviderTemplate?.DisplayNameKey.ToString();
        using var form = new ManageApiKeyForm(flow.Settings.Model.ApiKeys, defaultName, flow.Settings);
        await DialogManager
            .CreateDialog(form, LocaleKey.ApiKeyComboBox_ManageApiKey.I18N(), TopLevel.GetTopLevel(this))
            .WithPrimaryButton(LocaleKey.Common_OK.I18N())
            .ShowAsync(cancellationToken);
    }

    private void TokenBox_OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        foreach (var ch in e.Text)
        {
            if (!char.IsAsciiDigit(ch))
            {
                e.Handled = true;
                return;
            }
        }
    }
}
