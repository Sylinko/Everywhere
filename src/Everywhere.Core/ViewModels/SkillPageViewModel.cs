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

    private readonly ISkillManager _skillManager;
    private readonly BindableList<SkillSourceFilterItem> _sourceFilterItems = [];
    private readonly BindableList<SkillSourceGroupItem> _filteredSourceGroups = [];
    private readonly SourceCache<SkillDescriptorWrapper, string> _skills = new(static item => item.Skill.Id);
    private readonly Dictionary<string, bool> _expandedStateBySourceKey = new(StringComparer.OrdinalIgnoreCase);

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

    partial void OnSelectedSkillWrapperChanged(SkillDescriptorWrapper? oldValue, SkillDescriptorWrapper? newValue)
    {
        Console.WriteLine($"OnSelectedSkillWrapperChanged(oldValue: {oldValue?.Skill.FilePath}, newValue: {newValue?.Skill.FilePath}");
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
        var items = new List<SkillDescriptorWrapper>();
        for (var index = 0; index < _skillManager.SourceGroups.Count; index++)
        {
            var group = _skillManager.SourceGroups[index];
            items.AddRange(group.Skills.Select(skill => new SkillDescriptorWrapper(skill, group, index)));
        }

        _skills.Edit(updater =>
        {
            updater.Clear();
            updater.AddOrUpdate(items);
        });

        RebuildSourceFilters(items);
        OnPropertyChanged(nameof(HasAnySkills));
    }

    private void RebuildSourceFilters(IReadOnlyList<SkillDescriptorWrapper> skills)
    {
        var selectedSourceKey = SelectedSourceFilter?.SourceKey;
        _sourceFilterItems.Clear();
        _sourceFilterItems.Add(SkillSourceFilterItem.All);

        foreach (var item in skills
                     .AsValueEnumerable()
                     .GroupBy(static item => item.SourceKey)
                     .OrderBy(group => group.Min(item => item.SourceOrder))
                     .ThenBy(group => group.First().SourceName, StringComparer.OrdinalIgnoreCase))
        {
            var first = item.First();
            _sourceFilterItems.Add(new SkillSourceFilterItem(first.SourceKey, new DirectResourceKey(first.SourceName)));
        }

        SelectedSourceFilter =
            _sourceFilterItems.FirstOrDefault(item => item.SourceKey == selectedSourceKey) ??
            SkillSourceFilterItem.All;
    }

    private void HandleFilteredSkillsChanged(IReadOnlyCollection<SkillDescriptorWrapper> skills)
    {
        var grouped = skills
            .GroupBy(static item => item.SourceKey)
            .Select(group =>
            {
                var first = group.First();
                var groupItem = new SkillSourceGroupItem(
                    first.SourceKey,
                    first.SourceName,
                    group
                        .AsValueEnumerable()
                        .OrderBy(static item => item.Skill.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(static item => item.Skill.Id, StringComparer.OrdinalIgnoreCase)
                        .ToList());
                if (_expandedStateBySourceKey.TryGetValue(groupItem.SourceKey, out var isExpanded))
                {
                    groupItem.IsExpanded = isExpanded;
                }

                groupItem.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(SkillSourceGroupItem.IsExpanded))
                    {
                        _expandedStateBySourceKey[groupItem.SourceKey] = groupItem.IsExpanded;
                    }
                };
                return groupItem;
            })
            .OrderBy(static group => group.Items[0].SourceOrder)
            .ThenBy(static group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _filteredSourceGroups.Clear();
        foreach (var group in grouped)
        {
            _filteredSourceGroups.Add(group);
        }

        FilteredSkillCount = skills.Count;
        TrySelectSkill(SelectedSkillWrapper?.Id);
    }

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

    public sealed partial class SkillSourceGroupItem(
        string sourceKey,
        string name,
        IReadOnlyList<SkillDescriptorWrapper> items
    ) : ObservableObject
    {
        public string SourceKey { get; } = sourceKey;

        public string Name { get; } = name;

        public IReadOnlyList<SkillDescriptorWrapper> Items { get; } = items;

        public int Count => Items.Count;

        [ObservableProperty]
        public partial bool IsExpanded { get; set; } = true;
    }

    public sealed class SkillDescriptorWrapper : ObservableObject
    {
        public SkillDescriptor Skill { get; }

        public string Id => Skill.Id;

        public string SourceKey { get; }

        public string SourceName { get; }

        public int SourceOrder { get; }

        public IReadOnlyList<string> SearchValues { get; }

        public SkillDescriptorWrapper(SkillDescriptor skill, SkillSourceGroup group, int sourceOrder)
        {
            Skill = skill;
            SourceKey = $"{group.SourceRoot}:{group.DirectoryPath}";
            SourceName = group.Name;
            SourceOrder = sourceOrder;
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