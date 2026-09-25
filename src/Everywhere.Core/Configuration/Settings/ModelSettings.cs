using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.AI;

namespace Everywhere.Configuration;

public sealed partial class ModelSettings : SettingsBase
{
    public ObservableCollection<CustomAssistant> CustomAssistants
    {
        get;
        set
        {
            if (field == value) return;

            field?.CollectionChanged -= HandleCustomAssistantsChanged;
            field = value;
            value.CollectionChanged += HandleCustomAssistantsChanged;

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedCustomAssistant));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCustomAssistant))]
    public partial Guid SelectedCustomAssistantId { get; set; }

    /// <summary>
    /// Gets or sets the currently selected custom assistant via <see cref="SelectedCustomAssistantId"/>.
    /// Returns null when the persisted id no longer exists in the collection.
    /// Setting this property updates the persisted id.
    /// </summary>
    [JsonIgnore]
    public CustomAssistant? SelectedCustomAssistant
    {
        get => CustomAssistants.FirstOrDefault(a => a.Id == SelectedCustomAssistantId);
        set => SelectedCustomAssistantId = CustomAssistants.FirstOrDefault(a => a == value)?.Id ?? Guid.Empty;
    }

    [ObservableProperty]
    public partial ObservableCollection<ApiKey> ApiKeys { get; set; } = [];

    public ModelSettings(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        CustomAssistants = []; // Initialize the collection with event subscription
    }

    private void HandleCustomAssistantsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(SelectedCustomAssistant));
}