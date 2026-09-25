using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

/// <summary>
/// Base class for options owned by a model protocol schema.
/// </summary>
public abstract class ModelSchemaOptions : ObservableObject
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public abstract ModelProviderSchema Schema { get; }
}