using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Skills;
using ShadUI;

namespace Everywhere.ViewModels;

public sealed partial class SkillPageViewModel(ISkillManager skillManager) : BusyViewModelBase
{
    public ISkillManager SkillManager => skillManager;

    [ObservableProperty]
    public partial SkillDescriptor? SelectedSkill { get; set; }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task RefreshAsync(CancellationToken cancellationToken)
    {
        return ExecuteBusyTaskAsync(
            token => skillManager.RefreshAsync(token),
            ToastExceptionHandler,
            cancellationToken);
    }

    [RelayCommand]
    private async Task OpenSourceFolderAsync(SkillSourceGroup? group)
    {
        if (group is null) return;

        var launched = await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(group.DirectoryPath));
        if (!launched)
        {
            ToastManager.Error($"Failed to open folder: {group.DirectoryPath}");
        }
    }

    protected override void OnIsBusyChanged()
    {
        base.OnIsBusyChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }
}
