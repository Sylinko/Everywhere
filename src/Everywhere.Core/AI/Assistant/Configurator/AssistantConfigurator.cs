using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI.Configurator;

public abstract class AssistantConfigurator : ObservableValidator, IHaveSettingsItems
{
    [SettingsItemIgnore]
    public abstract SettingsItems SettingsItems { get; }

    /// <summary>
    /// Called before switching to another configurator type to backup necessary values.
    /// </summary>
    public abstract void Backup();

    /// <summary>
    /// Called to apply the configuration to the associated CustomAssistant.
    /// </summary>
    public abstract void Apply();

    /// <summary>
    /// Initializes the configurator by applying the current configuration values.
    /// </summary>
    public void Initialize()
    {
        Backup();
        Apply();
    }

    /// <summary>
    /// Validate the current configuration and show UI feedback if invalid.
    /// </summary>
    /// <returns>
    /// True if the configuration is valid; otherwise, false.
    /// </returns>
    public bool Validate()
    {
        ValidateAllProperties();
        return !HasErrors;
    }

    /// <summary>
    /// Resolves an assistant configuration based on the provided model specialization.
    /// </summary>
    /// <param name="specialization"></param>
    /// <returns></returns>
    public abstract Assistant ResolveAssistant(ModelSpecializations specialization);

    /// <summary>
    /// Backups of the original customizable values before switching to advanced configurator.
    /// Key: Property name
    /// Value: (DefaultValue, CustomValue)
    /// </summary>
    private readonly Dictionary<string, object?> _backups = new();

    /// <summary>
    /// When the user switches configurator types, we need to preserve the values set in the advanced configurator.
    /// This method helps to return the original customizable, while keeping a backup if needed.
    /// </summary>
    /// <param name="property"></param>
    /// <param name="propertyName"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    protected void Backup<T>(T property, [CallerArgumentExpression("property")] string propertyName = "")
    {
        _backups[propertyName] = property;
    }

    /// <summary>
    /// Restores the original customizable value if exists in backup, otherwise returns the provided property.
    /// </summary>
    /// <param name="property"></param>
    /// <param name="propertyName"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    protected T? Restore<T>(T property, [CallerArgumentExpression("property")] string propertyName = "")
    {
        return _backups.TryGetValue(propertyName, out var backup) ? (T?)backup : property;
    }
}