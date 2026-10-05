using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Configuration;
using Lucide.Avalonia;
using Serilog;
using ShadUI;

namespace Everywhere.ViewModels;

public partial class CustomAssistantPageViewModel : ReactiveViewModelBase
{
    private readonly IKernelMixinFactory _kernelMixinFactory;
    private readonly Settings _settings;

    public ObservableCollection<CustomAssistant> CustomAssistants => _settings.Model.CustomAssistants;

    [ObservableProperty]
    public partial CustomAssistant? SelectedCustomAssistant { get; set; }

    public CustomAssistantPageViewModel(IKernelMixinFactory kernelMixinFactory, Settings settings)
    {
        _kernelMixinFactory = kernelMixinFactory;
        _settings = settings;
        SelectedCustomAssistant = settings.Model.SelectedCustomAssistant ?? settings.Model.CustomAssistants.FirstOrDefault();
    }

    protected internal override void OnNavigatedTo(IReadOnlyList<string> remainingSegments)
    {
        if (remainingSegments.Count == 0)
        {
            return;
        }

        var assistantIdText = remainingSegments[0];
        if (!Guid.TryParse(assistantIdText, out var assistantId))
        {
            ClearSelectedAssistant();
            ToastHost
                .CreateToast(
                    LocaleKey.Common_Warning.I18N(),
                    new FormattedDynamicLocaleKey(
                        LocaleKey.CustomAssistantPage_InvalidRouteAssistant_Content,
                        new DirectLocaleKey(assistantIdText)))
                .DismissOnClick()
                .ShowWarning();
            return;
        }

        if (!SelectAssistant(assistantId))
        {
            ToastHost
                .CreateToast(
                    LocaleKey.Common_Warning.I18N(),
                    new FormattedDynamicLocaleKey(
                        LocaleKey.CustomAssistantPage_MissingRouteAssistant_Content,
                        new DirectLocaleKey(assistantId)))
                .DismissOnClick()
                .ShowWarning();
        }
    }

    private static Color[] RandomAssistantIconBackgrounds { get; } =
    [
        Colors.MediumPurple,
        Colors.CadetBlue,
        Colors.Coral,
        Colors.CornflowerBlue,
        Colors.DarkCyan,
        Colors.DarkGoldenrod,
        Colors.DarkKhaki,
        Colors.DarkOrange,
        Colors.DarkSalmon,
        Colors.DarkSeaGreen,
        Colors.DarkTurquoise,
        Colors.DeepSkyBlue,
        Colors.DodgerBlue,
        Colors.ForestGreen,
        Colors.Goldenrod,
        Colors.IndianRed,
        Colors.LightCoral,
        Colors.LightSeaGreen,
        Colors.MediumSeaGreen,
        Colors.MediumSlateBlue,
        Colors.MediumTurquoise,
        Colors.OliveDrab,
        Colors.OrangeRed,
        Colors.RoyalBlue,
        Colors.SeaGreen,
        Colors.SteelBlue,
    ];

    [RelayCommand]
    private void CreateNewCustomAssistant()
    {
        var newAssistant = new CustomAssistant
        {
            Name = LocaleKey.CustomAssistant_Name_Default.I18N(),
            Configuration = new PresetAssistantConfiguration(),
            Icon = new ColoredIcon(
                ColoredIconType.Lucide,
                background: RandomAssistantIconBackgrounds[Random.Shared.Next(RandomAssistantIconBackgrounds.Length)])
            {
                Kind = LucideIconKind.Bot
            }
        };
        _settings.Model.CustomAssistants.Add(newAssistant);
        _settings.Model.SelectedCustomAssistant ??= newAssistant;
        SelectedCustomAssistant = newAssistant;
    }

    [RelayCommand]
    private void DuplicateCustomAssistant()
    {
        if (SelectedCustomAssistant is not { } customAssistant) return;

        var options = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            IgnoreReadOnlyProperties = true
        };
        var json = JsonSerializer.Serialize(customAssistant, options);
        var duplicatedAssistant = JsonSerializer.Deserialize<CustomAssistant>(json, options).NotNull();

        duplicatedAssistant.Id = Guid.CreateVersion7();
        duplicatedAssistant.Name += " - " + LocaleKey.Common_Copy.I18N();
        _settings.Model.CustomAssistants.Insert(_settings.Model.CustomAssistants.IndexOf(customAssistant) + 1, duplicatedAssistant);
        SelectedCustomAssistant = duplicatedAssistant;
    }

    [RelayCommand]
    private async Task CheckConnectivityAsync(CancellationToken cancellationToken)
    {
        if (SelectedCustomAssistant is not { } customAssistant) return;
        if (!customAssistant.Configuration.Validate()) return;

        KernelMixin? kernelMixin = null;
        try
        {
            kernelMixin = _kernelMixinFactory.Create(customAssistant);
            await kernelMixin.CheckConnectivityAsync(cancellationToken);
            ToastHost
                .CreateToast(LocaleKey.CustomAssistantPageViewModel_CheckConnectivity_SuccessToast_Title.I18N())
                .DismissOnClick()
                .ShowSuccess();
        }
        catch (Exception ex)
        {
            ex = ChatExceptionNormalizer.Handle(ex, kernelMixin);
            Log.Logger.ForContext<CustomAssistantPageViewModel>().Error(
                ex,
                "Failed to check connectivity key for endpoint {ProviderId} and model {ModelId}",
                customAssistant.Configuration.Endpoint,
                customAssistant.Configuration.ModelId);
            ToastHost
                .CreateToast(LocaleKey.CustomAssistantPageViewModel_CheckConnectivity_FailedToast_Title.I18N())
                .WithContent(ex.GetFriendlyMessage().ToTextBlock())
                .DismissOnClick()
                .ShowError();
        }
        finally
        {
            kernelMixin?.Dispose();
        }
    }

    [RelayCommand]
    private async Task DeleteCustomAssistantAsync()
    {
        if (SelectedCustomAssistant is not { } customAssistant) return;
        var result = await DialogHost.CreateDialog(
                LocaleKey.CustomAssistantPageViewModel_DeleteCustomAssistant_Dialog_Message.I18N().Format(customAssistant.Name),
                LocaleKey.Common_Warning.I18N())
            .WithPrimaryButton(LocaleKey.Common_Yes.I18N())
            .WithCancelButton(LocaleKey.Common_No.I18N())
            .ShowAsync();
        if (result != DialogResult.Primary) return;

        _settings.Model.CustomAssistants.Remove(customAssistant);
        _settings.Model.SelectedCustomAssistant = _settings.Model.CustomAssistants.FirstOrDefault();
    }

    private bool SelectAssistant(Guid assistantId)
    {
        if (CustomAssistants.FirstOrDefault(a => a.Id == assistantId) is { } assistant)
        {
            SelectedCustomAssistant = assistant;
            _settings.Model.SelectedCustomAssistant = assistant;
            return true;
        }

        ClearSelectedAssistant();
        return false;
    }

    private void ClearSelectedAssistant()
    {
        SelectedCustomAssistant = null;
        _settings.Model.SelectedCustomAssistant = null;
    }
}
