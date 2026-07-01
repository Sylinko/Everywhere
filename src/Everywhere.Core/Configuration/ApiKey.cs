using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GnomeStack.Os.Secrets;
using Serilog;

namespace Everywhere.Configuration;

/// <summary>
/// Represents an API key with a friendly name and secure storage for the key itself.
/// When serialized, only the Id and Name are stored. The actual key is stored securely using OsSecretVault.
/// </summary>
public partial class ApiKey : ObservableValidator
{
    /// <summary>
    /// The service name used for secure storage of API keys.
    /// </summary>
    private const string ServiceName = "com.sylinko.everywhere";

    public static ApiKey Empty { get; } = new()
    {
        Id = Guid.Empty,
        Name = LocaleResolver.ApiKey_EmptyName
    };

    /// <summary>
    /// Retrieves the API key from secure storage based on the given ID.
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    public static string? GetKey(Guid id) => id == Guid.Empty ? null : OsSecretVault.GetSecret(ServiceName, id.ToString("N"));

    /// <summary>
    /// Deletes the API key from secure storage based on the given ID.
    /// </summary>
    /// <param name="id"></param>
    public static void DeleteKey(Guid id) => OsSecretVault.DeleteSecret(ServiceName, id.ToString("N"));

    public Guid Id { get; init; } = Guid.CreateVersion7();

    [ObservableProperty]
    [CustomValidation(typeof(ApiKey), nameof(ValidateName))]
    public partial string? Name { get; set; }

    [JsonIgnore]
    [CustomValidation(typeof(ApiKey), nameof(ValidateKey))]
    public string? SecretKey
    {
        get => _pendingKey ?? GetKey(Id);
        set => SetProperty(ref _pendingKey, value);
    }

    /// <summary>
    /// You may say: Why don't use the SecretString?
    /// The reason is said at: https://github.com/dotnet/platform-compat/blob/master/docs/DE0001.md
    /// TL;DR: SecretString has no native support on any OS.
    /// </summary>
    private string? _pendingKey;

    /// <summary>
    /// Validates the API key by checking if it is not null and empty.
    /// </summary>
    /// <param name="apiKey"></param>
    /// <returns></returns>
    public static ValidationResult? Validate(Guid apiKey)
    {
        if (GetKey(apiKey).IsNullOrWhiteSpace())
        {
            return new ValidationResult(LocaleResolver.ValidationErrorMessage_RequiredApiKey);
        }

        return ValidationResult.Success;
    }

    /// <summary>
    /// Validates the Name property. Used for CustomValidation attribute.
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public static ValidationResult? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ValidationResult(LocaleResolver.ValidationErrorMessage_Required);
        }

        if (name.Length > 50)
        {
            return new ValidationResult(string.Format(LocaleResolver.ValidationErrorMessage_MaxLength, 50));
        }

        return ValidationResult.Success;
    }

    /// <summary>
    /// Validates the Key property. Used for CustomValidation attribute.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public static ValidationResult? ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return new ValidationResult(LocaleResolver.ValidationErrorMessage_Required);
        }

        if (key.Length > 65535)
        {
            return new ValidationResult(string.Format(LocaleResolver.ValidationErrorMessage_MaxLength, 65535));
        }

        return ValidationResult.Success;
    }

    [JsonIgnore]
    [field: AllowNull, MaybeNull]
    public ICommand ValidateAndSaveCommand => field ??= new RelayCommand(() => ValidateAndSave());

    /// <summary>
    /// Validates the entire configuration. returns true if valid.
    /// </summary>
    /// <returns></returns>
    public bool ValidateAndSave()
    {
        if (Id == Guid.Empty)
        {
            throw new InvalidOperationException("Cannot save an ApiKey with an empty Id.");
        }

        ValidateAllProperties();
        if (HasErrors || _pendingKey.IsNullOrEmpty()) return false;

        try
        {
            OsSecretVault.SetSecret(ServiceName, Id.ToString("N"), _pendingKey);
        }
        catch (Exception ex)
        {
            Log.ForContext<ApiKey>().Error(ex, "An error occurred while saving the ApiKey.");
            return false;
        }

        _pendingKey = null;
        return true;
    }

    public override bool Equals(object? obj)
    {
        return obj is ApiKey other && Id == other.Id;
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    public override string ToString()
    {
        return Name ?? $"ApiKey-{Id}";
    }

    // implement == and != operators
    public static bool operator ==(ApiKey? left, ApiKey? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        return left.Id == right.Id;
    }

    public static bool operator !=(ApiKey? left, ApiKey? right)
    {
        return !(left == right);
    }
}