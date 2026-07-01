using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Cloud;

/// <summary>
/// Represents the user's profile information.
/// </summary>
public sealed partial class UserProfile : ObservableObject
{
    [ObservableProperty]
    [JsonPropertyName("name")]
    public required partial string Name { get; set; }

    [ObservableProperty]
    [JsonPropertyName("email")]
    public required partial string Email { get; set; }

    [ObservableProperty]
    [JsonPropertyName("picture")]
    public partial string? AvatarUrl { get; set; }
}

[JsonSerializable(typeof(UserProfile))]
public sealed partial class UserProfileJsonSerializerContext : JsonSerializerContext;