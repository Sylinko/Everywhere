using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Skills;
using ShadUI;
using ZLinq;
using AbstractionsLocaleKey = Everywhere.Abstractions.I18N.LocaleKey;

namespace Everywhere.ViewModels;

public sealed partial class SkillPageViewModel : BusyViewModelBase
{
    [ObservableProperty]
    public partial SkillDescriptorWrapper? SelectedSkillWrapper { get; set; }

    public string? SearchText
    {
        get;
        set
        {
            if (SetProperty(ref field, value)) RefreshFilter();
        }
    }

    public SkillSourceFilterItem? SelectedSourceFilter
    {
        get;
        set
        {
            if (SetProperty(ref field, value)) RefreshFilter();
        }
    }

    [ObservableProperty]
    public partial int FilteredSkillCount { get; private set; }

    [ObservableProperty]
    public partial IDynamicResourceKey FilteredSkillCountKey { get; private set; } =
        new FormattedDynamicResourceKey(LocaleKey.SkillPage_CountText, new DirectResourceKey(0));

    public IReadOnlyBindableList<SkillSourceFilterItem> SourceFilterItems { get; }

    public IReadOnlyBindableList<SkillSourceGroupItem> FilteredSourceGroups { get; }

    public IEnumerable<SkillInformationField> SelectedSkillInformationFields
    {
        get
        {
            if (SelectedSkillWrapper is not { Skill: { } skill }) yield break;

            if (TryCreateField(LocaleKey.SkillPage_Field_Id, skill.Id, true, out var id)) yield return id;
            if (TryCreateField(LocaleKey.SkillPage_Field_Source, skill.SourceName, false, out var source)) yield return source;
            if (TryCreateField(LocaleKey.SkillPage_Field_File, skill.FilePath, true, out var file)) yield return file;
            if (TryCreateField(LocaleKey.SkillPage_Field_License, skill.License, false, out var license)) yield return license;
            if (TryCreateField(LocaleKey.SkillPage_Field_Compatibility, skill.Compatibility, false, out var compat)) yield return compat;
            if (TryCreateField(LocaleKey.SkillPage_Field_Author, skill.Author, false, out var author)) yield return author;
            if (TryCreateField(LocaleKey.SkillPage_Field_Version, skill.Version, false, out var version)) yield return version;

            bool TryCreateField(object labelKey, string? value, bool isMonospace, [NotNullWhen(true)] out SkillInformationField? result)
            {
                result = null;
                if (string.IsNullOrWhiteSpace(value)) return false;
                result = new SkillInformationField(new DynamicResourceKey(labelKey), value, isMonospace);
                return true;
            }
        }
    }

    [ObservableProperty]
    public partial bool IsMarkdownPreviewMode { get; set; }

    public bool HasAnySkills => _skills.Count > 0;

    public bool HasVisibleSourceGroups => _filteredSourceGroups.Count > 0;

    private readonly ISkillManager _skillManager;
    private readonly BindableList<SkillSourceFilterItem> _sourceFilterItems = [];
    private readonly BindableList<SkillSourceGroupItem> _filteredSourceGroups = [];
    private readonly SourceCache<SkillDescriptorWrapper, string> _skills = new(static item => item.Skill.Id);
    private readonly Dictionary<string, SkillSourceGroupItem> _sourceGroupItemsByKey = new(StringComparer.OrdinalIgnoreCase);

    public SkillPageViewModel(ISkillManager skillManager)
    {
        _skillManager = skillManager;
        SourceFilterItems = _sourceFilterItems;
        FilteredSourceGroups = _filteredSourceGroups;

        _sourceFilterItems.Add(SkillSourceFilterItem.All);
        SelectedSourceFilter = SkillSourceFilterItem.All;

        LifetimeDisposables.Add(_skills);
        _skills
            .Connect()
            .AutoRefresh(static wrapper => wrapper.Skill.IsEnabled)
            .Filter(FilterSkill)
            .ToCollection()
            .ObserveOnAvaloniaDispatcher()
            .Subscribe(HandleFilteredSkillsChanged)
            .DisposeWith(LifetimeDisposables);

        SyncSkillsFromManager();
        skillManager.SourceGroups.CollectionChanged += HandleSourceGroupsCollectionChanged;
        LifetimeDisposables.Add(Disposable.Create(() => skillManager.SourceGroups.CollectionChanged -= HandleSourceGroupsCollectionChanged));
    }

    partial void OnFilteredSkillCountChanged(int value)
    {
        FilteredSkillCountKey = new FormattedDynamicResourceKey(
            LocaleKey.SkillPage_CountText,
            new DirectResourceKey(value));
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task RefreshAsync(CancellationToken cancellationToken)
    {
        var selectedSkillId = SelectedSkillWrapper?.Id;
        return ExecuteBusyTaskAsync(
            async token =>
            {
                await _skillManager.RefreshAsync(token);
                SyncSkillsFromManager();
                TrySelectSkill(selectedSkillId);
                ToastManager.Success(LocaleResolver.SkillPage_RescanSuccessToast_Title);
            },
            ToastExceptionHandler,
            cancellationToken);
    }

    [RelayCommand]
    private async Task OpenFolderAsync(string folderPath)
    {
        var launched = await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folderPath));
        if (!launched)
        {
            ToastManager.Error(LocaleResolver.Common_Error, LocaleResolver.SkillPage_OpenFileLocationFailedToast_Title);
        }
    }

    [RelayCommand]
    private async Task OpenSelectedSkillFolderAsync()
    {
        if (SelectedSkillWrapper is null) return;

        var directoryPath = Path.GetDirectoryName(SelectedSkillWrapper.Skill.FilePath);
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            ToastManager.Error(LocaleResolver.Common_Error, LocaleResolver.SkillPage_OpenFileLocationFailedToast_Title);
            return;
        }

        var launched = await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(directoryPath));
        if (!launched)
        {
            ToastManager.Error(LocaleResolver.Common_Error, LocaleResolver.SkillPage_OpenFileLocationFailedToast_Title);
        }
    }

    [RelayCommand]
    private async Task CopySkillIdAsync()
    {
        if (SelectedSkillWrapper is null) return;
        await CopyTextAsync(SelectedSkillWrapper.Id);
    }

    [RelayCommand]
    private async Task CopySkillFilePathAsync()
    {
        if (SelectedSkillWrapper is null) return;
        await CopyTextAsync(SelectedSkillWrapper.Skill.FilePath);
    }

    [RelayCommand]
    private async Task CopyMarkdownAsync()
    {
        if (SelectedSkillWrapper?.Skill.MarkdownContent is not { Length: > 0 } markdown) return;
        await CopyTextAsync(markdown);
    }

    protected override void OnIsBusyChanged()
    {
        base.OnIsBusyChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private async static Task CopyTextAsync(string text)
    {
        try
        {
            await App.Clipboard.SetTextAsync(text);
            ToastManager.Success(DynamicResourceKey.Resolve(AbstractionsLocaleKey.Common_Copied));
        }
        catch (Exception ex)
        {
            ToastManager.Error(LocaleResolver.Common_Error, ex.GetFriendlyMessage());
        }
    }

    private void HandleSourceGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var selectedSkillId = SelectedSkillWrapper?.Id;
        SyncSkillsFromManager();
        TrySelectSkill(selectedSkillId);
    }

    private void SyncSkillsFromManager()
    {
        SyncSourceGroupsFromManager();

        var items = new List<SkillDescriptorWrapper>();
        foreach (var group in _skillManager.SourceGroups)
        {
            items.AddRange(group.Skills.Select(skill => new SkillDescriptorWrapper(skill, group)));
        }

        _skills.Edit(updater =>
        {
            updater.Clear();
            updater.AddOrUpdate(items);
        });

        RebuildSourceFilters();
        ApplyFilteredSkills(_skills.Items.Where(FilterSkill).ToList());
        OnPropertyChanged(nameof(HasAnySkills));
    }

    private void SyncSourceGroupsFromManager()
    {
        var activeSourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceGroup in _skillManager.SourceGroups)
        {
            var sourceKey = GetSourceKey(sourceGroup);
            activeSourceKeys.Add(sourceKey);

            if (_sourceGroupItemsByKey.TryGetValue(sourceKey, out var groupItem))
            {
                groupItem.Update(sourceGroup.Name, sourceGroup.DirectoryPath);
            }
            else
            {
                _sourceGroupItemsByKey.Add(sourceKey, new SkillSourceGroupItem(sourceGroup.Name, sourceGroup.DirectoryPath));
            }
        }

        foreach (var sourceKey in _sourceGroupItemsByKey.Keys.Where(key => !activeSourceKeys.Contains(key)).ToArray())
        {
            _sourceGroupItemsByKey.Remove(sourceKey);
        }
    }

    private void RebuildSourceFilters()
    {
        var selectedSourceKey = SelectedSourceFilter?.SourceKey;
        _sourceFilterItems.Clear();
        _sourceFilterItems.Add(SkillSourceFilterItem.All);

        foreach (var group in _skillManager.SourceGroups)
        {
            _sourceFilterItems.Add(new SkillSourceFilterItem(GetSourceKey(group), new DirectResourceKey(group.Name)));
        }

        SelectedSourceFilter =
            _sourceFilterItems.FirstOrDefault(item => item.SourceKey == selectedSourceKey) ??
            SkillSourceFilterItem.All;
    }

    private void HandleFilteredSkillsChanged(IReadOnlyCollection<SkillDescriptorWrapper> skills)
    {
        ApplyFilteredSkills(skills);
    }

    private void ApplyFilteredSkills(IReadOnlyCollection<SkillDescriptorWrapper> skills)
    {
        var skillsBySourceKey = skills
            .GroupBy(static item => item.SourceKey)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var visibleSourceGroups = _skillManager.SourceGroups
            .AsValueEnumerable()
            .Where(IsSourceGroupVisible)
            .ToList();

        foreach (var sourceGroup in visibleSourceGroups)
        {
            var sourceKey = GetSourceKey(sourceGroup);
            if (!_sourceGroupItemsByKey.TryGetValue(sourceKey, out var groupItem))
            {
                continue;
            }

            skillsBySourceKey.TryGetValue(sourceKey, out var sourceSkills);
            groupItem.SetItems(
                sourceSkills?
                    .AsValueEnumerable()
                    .OrderBy(static item => item.Skill.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.Skill.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? []);
        }

        _filteredSourceGroups.Clear();
        foreach (var sourceGroup in visibleSourceGroups)
        {
            var sourceKey = GetSourceKey(sourceGroup);
            if (_sourceGroupItemsByKey.TryGetValue(sourceKey, out var groupItem))
            {
                _filteredSourceGroups.Add(groupItem);
            }
        }

        OnPropertyChanged(nameof(HasVisibleSourceGroups));
        FilteredSkillCount = skills.Count;
        TrySelectSkill(SelectedSkillWrapper?.Id);
    }

    private bool IsSourceGroupVisible(SkillSourceGroup sourceGroup) =>
        SelectedSourceFilter is not { IsAll: false } sourceFilter ||
        GetSourceKey(sourceGroup).Equals(sourceFilter.SourceKey, StringComparison.OrdinalIgnoreCase);

    private bool FilterSkill(SkillDescriptorWrapper item)
    {
        if (SelectedSourceFilter is { IsAll: false } sourceFilter &&
            !item.SourceKey.Equals(sourceFilter.SourceKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var search = SearchText;
        return string.IsNullOrWhiteSpace(search) ||
            item.SearchValues.AsValueEnumerable().Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshFilter() => _skills.Refresh();

    private static string GetSourceKey(SkillSourceGroup group) => $"{group.SourceRoot}:{group.DirectoryPath}";

    /// <summary>
    /// Tries to select a skill by the preferred skill ID. If not found, selects the first skill in the filtered list.
    /// </summary>
    /// <param name="preferredSkillId"></param>
    private void TrySelectSkill(string? preferredSkillId)
    {
        SelectedSkillWrapper = (preferredSkillId is null ?
                null :
                _filteredSourceGroups
                    .AsValueEnumerable()
                    .SelectMany(static group => group.Items)
                    .FirstOrDefault(item => item.Id.Equals(preferredSkillId, StringComparison.OrdinalIgnoreCase))) ??
            _filteredSourceGroups
                .AsValueEnumerable()
                .SelectMany(static group => group.Items)
                .FirstOrDefault();
    }

    public sealed record SkillSourceFilterItem(string SourceKey, IDynamicResourceKey Name)
    {
        public static SkillSourceFilterItem All { get; } =
            new("all", new DynamicResourceKey(LocaleKey.SkillPage_SourceFilter_All));

        public bool IsAll => ReferenceEquals(this, All);
    }

    public sealed partial class SkillSourceGroupItem(string name, string sourceDirectoryPath) : ObservableObject
    {
        private readonly BindableList<SkillDescriptorWrapper> _items = [];

        [ObservableProperty]
        public partial string Name { get; private set; } = name;

        [ObservableProperty]
        public partial string SourceDirectoryPath { get; private set; } = sourceDirectoryPath;

        public IReadOnlyBindableList<SkillDescriptorWrapper> Items => _items;

        public int Count => Items.Count;

        [ObservableProperty]
        public partial bool IsExpanded { get; set; } = true;

        public void Update(string name, string sourceDirectoryPath)
        {
            Name = name;
            SourceDirectoryPath = sourceDirectoryPath;
        }

        public void SetItems(IReadOnlyCollection<SkillDescriptorWrapper> items)
        {
            _items.Clear();
            _items.AddRange(items);
            OnPropertyChanged(nameof(Count));
        }
    }

    public sealed class SkillDescriptorWrapper : ObservableObject
    {
        public SkillDescriptor Skill { get; }

        public string Id => Skill.Id;

        public string SourceKey { get; }

        public IReadOnlyList<string> SearchValues { get; }

        public SkillDescriptorWrapper(SkillDescriptor skill, SkillSourceGroup group)
        {
            Skill = skill;
            SourceKey = GetSourceKey(group);
            SearchValues =
            [
                skill.Id,
                skill.Name,
                skill.Description ?? string.Empty,
                skill.FilePath,
                skill.SourceName,
                skill.License ?? string.Empty,
                skill.Compatibility ?? string.Empty,
                skill.Author ?? string.Empty,
                skill.Version ?? string.Empty,
                .. skill.Metadata.SelectMany(static kvp => new[] { kvp.Key, kvp.Value })
            ];
        }
    }

    public sealed record SkillInformationField(IDynamicResourceKey Label, string Value, bool IsMonospace);
}
