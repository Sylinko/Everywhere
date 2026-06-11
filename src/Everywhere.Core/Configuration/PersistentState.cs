using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.AI;
using Everywhere.Chat;

namespace Everywhere.Configuration;

/// <summary>
/// Represents the persistent state of the application.
/// </summary>
public class PersistentState(IKeyValueStorage storage) : ObservableObject
{
    /// <summary>
    /// Used to popup welcome dialog on first launch and update.
    /// </summary>
    public string? PreviousLaunchVersion
    {
        get => Get<string?>();
        set => Set(value);
    }

    /// <summary>
    /// Pop a tray notification when the application is launched for the first time.
    /// </summary>
    public bool IsHideToTrayIconNotificationShown
    {
        get => Get(true);
        set => Set(value);
    }

    public bool IsToolCallEnabled
    {
        get => Get(true);
        set => Set(value);
    }

    public bool IsWebSearchEnabled
    {
        get => Get(false);
        set => Set(value);
    }

    public int MaxChatAttachmentCount
    {
        get => Get(50);
        set => Set(value);
    }

    public bool IsMainViewSidebarExpanded
    {
        get => Get(true);
        set => Set(value);
    }

    public bool? IsChatWindowPinned
    {
        get => Get<bool?>();
        set => Set(value);
    }

    public bool IsChatWindowHistoryOpened
    {
        get => Get<bool>();
        set => Set(value);
    }

    public double ChatWindowHistoryDrawerWidth
    {
        get => Get(300d).FiniteOrDefault(300d);
        set => Set(value.FiniteOrDefault(300d));
    }

    public double ChatWindowHistoryDrawerHeight
    {
        get => Get(300d).FiniteOrDefault(300d);
        set => Set(value.FiniteOrDefault(300d));
    }

    public string? ChatInputAreaText
    {
        get => Get<string?>();
        set => Set(value);
    }

    public VisualContextDetailLevel VisualContextDetailLevel
    {
        get => Get(VisualContextDetailLevel.Compact);
        set => Set(value);
    }

    public VisualContextLengthLimit VisualContextLengthLimit
    {
        get => Get(VisualContextLengthLimit.Balanced);
        set => Set(value);
    }

    public int MaxContextRounds
    {
        get => Get(-1);
        set => Set(Math.Clamp(value, -1, 30));
    }

    public IReadOnlyList<ModelDefinitionTemplate>? OfficialModelDefinitionTemplate
    {
        get => Get<IReadOnlyList<ModelDefinitionTemplate>>();
        set => Set(value);
    }

    public IReadOnlyList<string>? DismissedOfficialModelWarningKeys
    {
        get => Get<IReadOnlyList<string>>();
        set => Set(value);
    }

    public Dictionary<string, bool>? SkillEnabledOverrides
    {
        get => Get<Dictionary<string, bool>>();
        set => Set(value);
    }

    public bool IsCloudSyncEnabled
    {
        get => Get(false);
        set => Set(value);
    }

    public DateTimeOffset? LastCloudSynchronized
    {
        get => Get<DateTimeOffset?>();
        set => Set(value);
    }

    public IDynamicResourceKey? LastCloudSynchronizationErrorMessageKey
    {
        get => Get<IDynamicResourceKey?>();
        set => Set(value);
    }

    private T? Get<T>(T? defaultValue = default, [CallerMemberName] string key = "")
    {
        return storage.Get(key, defaultValue);
    }

    private void Set<T>(T? value, [CallerMemberName] string key = "")
    {
        if (storage.Contains(key) &&
            EqualityComparer<T>.Default.Equals(storage.Get<T>(key), value)) return;

        storage.Set(key, value);
        OnPropertyChanged(key);
    }
}
