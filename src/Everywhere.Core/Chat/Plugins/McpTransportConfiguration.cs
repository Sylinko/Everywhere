using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;

namespace Everywhere.Chat.Plugins;

[SettingsSerializedSubtree]
[JsonPolymorphic]
[JsonDerivedType(typeof(StdioMcpTransportConfiguration), "stdio")]
[JsonDerivedType(typeof(HttpMcpTransportConfiguration), "sse")]
public abstract partial class McpTransportConfiguration : ObservableValidator
{
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(McpTransportConfiguration), nameof(ValidateName))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Description { get; set; }

    /// <summary>
    /// Validates the entire configuration. returns true if valid.
    /// </summary>
    /// <returns></returns>
    public bool Validate()
    {
        ValidateAllProperties();
        return !HasErrors;
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
            return new ValidationResult(LocaleKey.ValidationErrorMessage_Required.I18N());
        }

        if (name.Length > 50)
        {
            return new ValidationResult(string.Format(LocaleKey.ValidationErrorMessage_MaxLength.I18N(), 50));
        }

        return ValidationResult.Success;
    }

    /// <summary>
    /// Validates a required value and resolves the error in the current language.
    /// </summary>
    public static ValidationResult? ValidateRequired(string? value) => new RequiredAttribute().IsValid(value)
        ? ValidationResult.Success
        : new ValidationResult(LocaleKey.ValidationErrorMessage_Required.I18N());
}

public sealed partial class StdioMcpTransportConfiguration : McpTransportConfiguration
{
    public StdioMcpTransportConfiguration()
    {
        Arguments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SerializableArguments));
        EnvironmentVariables.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SerializableEnvironmentVariables));
    }

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(McpTransportConfiguration), nameof(ValidateRequired))]
    public partial string Command { get; set; } = string.Empty;

    [JsonPropertyName("Arguments")]
    [ConfigurationKeyName("Arguments")]
    public IReadOnlyList<string>? SerializableArguments
    {
        get => Arguments.AsValueEnumerable().Select(arg => arg.Value).ToArray();
        set
        {
            if (value is null)
            {
                Arguments.Clear();
            }
            else
            {
                Arguments.Reset(value.Select(arg => new BindingWrapper<string>(arg)));
            }
        }
    }

    [JsonIgnore]
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(StdioMcpTransportConfiguration), nameof(ValidateArguments))]
    public partial ObservableCollection<BindingWrapper<string>> Arguments { get; set; } = [];

    [ObservableProperty]
    public partial string? WorkingDirectory { get; set; }

    [JsonPropertyName("EnvironmentVariables")]
    [ConfigurationKeyName("EnvironmentVariables")]
    public IReadOnlyDictionary<string, string?>? SerializableEnvironmentVariables
    {
        get => EnvironmentVariables.AsValueEnumerable().DistinctBy(kvp => kvp.Key).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        set
        {
            if (value is null)
            {
                EnvironmentVariables.Clear();
            }
            else
            {
                EnvironmentVariables.Reset(value.Select(kvp => new ObservableKeyValuePair<string, string?>(kvp.Key, kvp.Value)));
            }
        }
    }

    [JsonIgnore]
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyPropertyChangedFor(nameof(SerializableEnvironmentVariables))]
    [CustomValidation(typeof(StdioMcpTransportConfiguration), nameof(ValidateEnvironmentVariables))]
    public partial ObservableCollection<ObservableKeyValuePair<string, string?>> EnvironmentVariables { get; set; } = [];

    [RelayCommand]
    private void AddEmptyArgument(int? index)
    {
        if (index is null || index < 0 || index >= Arguments.Count)
        {
            Arguments.Add(string.Empty);
        }
        else
        {
            Arguments.Insert(index.Value + 1, string.Empty);
        }
    }

    [RelayCommand]
    private void RemoveArgument(int index) => Arguments.SafeRemoveAt(index);

    [RelayCommand]
    private void AddEmptyEnvironmentVariable() => EnvironmentVariables.Add(new ObservableKeyValuePair<string, string?>(string.Empty, null));

    [RelayCommand]
    private void RemoveEnvironmentVariable(int index) => EnvironmentVariables.SafeRemoveAt(index);

    public static ValidationResult? ValidateArguments(ObservableCollection<BindingWrapper<string>>? input) =>
        input?.AsValueEnumerable().Any(bindingWrapper => bindingWrapper.Value.IsNullOrEmpty()) is true ?
            new ValidationResult(LocaleKey.ValidationErrorMessage_NullKey.I18N()) :
            ValidationResult.Success;

    public static ValidationResult? ValidateEnvironmentVariables(ObservableCollection<ObservableKeyValuePair<string, string?>>? input)
    {
        if (input is null) return ValidationResult.Success;

        var keys = new HashSet<string?>();
        foreach (var kvp in input.AsValueEnumerable())
        {
            if (kvp.Key.IsNullOrWhiteSpace())
            {
                return new ValidationResult(LocaleKey.ValidationErrorMessage_NullKey.I18N());
            }

            if (!keys.Add(kvp.Key))
            {
                return new ValidationResult(LocaleKey.ValidationErrorMessage_DuplicateKey.I18N());
            }
        }

        return ValidationResult.Success;
    }
}

public sealed partial class HttpMcpTransportConfiguration : McpTransportConfiguration
{
    public HttpMcpTransportConfiguration()
    {
        Headers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SerializableHeaders));
    }

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(HttpMcpTransportConfiguration), nameof(ValidateUrl))]
    [CustomValidation(typeof(McpTransportConfiguration), nameof(ValidateRequired))]
    public partial string Endpoint { get; set; } = string.Empty;

    [JsonPropertyName("Headers")]
    [ConfigurationKeyName("Headers")]
    public IReadOnlyDictionary<string, string>? SerializableHeaders
    {
        get => Headers.AsValueEnumerable().DistinctBy(kvp => kvp.Key).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        set
        {
            if (value is null)
            {
                Headers.Clear();
            }
            else
            {
                Headers.Reset(value.Select(kvp => new ObservableKeyValuePair<string, string>(kvp.Key, kvp.Value)));
            }
        }
    }

    [JsonIgnore]
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [NotifyPropertyChangedFor(nameof(SerializableHeaders))]
    [CustomValidation(typeof(HttpMcpTransportConfiguration), nameof(ValidateHeaders))]
    public partial ObservableCollection<ObservableKeyValuePair<string, string>> Headers { get; set; } = [];

    [ObservableProperty]
    public partial HttpTransportMode TransportMode { get; set; }

    /// <summary>
    /// Validates an endpoint using the standard URL validator and a localized error message.
    /// </summary>
    public static ValidationResult? ValidateUrl(string? value) => new UrlAttribute().IsValid(value)
        ? ValidationResult.Success
        : new ValidationResult(LocaleKey.ValidationErrorMessage_Url.I18N());

    [RelayCommand]
    private void AddEmptyHeader() => Headers.Add(new ObservableKeyValuePair<string, string>(string.Empty, string.Empty));

    [RelayCommand]
    private void RemoveHeader(int index) => Headers.SafeRemoveAt(index);

    public static ValidationResult? ValidateHeaders(ObservableCollection<ObservableKeyValuePair<string, string>>? headers)
    {
        if (headers is null) return ValidationResult.Success;

        var keys = new HashSet<string?>();
        foreach (var kvp in headers.AsValueEnumerable())
        {
            if (string.IsNullOrWhiteSpace(kvp.Key))
            {
                return new ValidationResult(LocaleKey.ValidationErrorMessage_NullKey.I18N());
            }

            if (string.IsNullOrWhiteSpace(kvp.Value))
            {
                return new ValidationResult(LocaleKey.ValidationErrorMessage_NullValue.I18N());
            }

            if (!keys.Add(kvp.Key))
            {
                return new ValidationResult(LocaleKey.ValidationErrorMessage_DuplicateKey.I18N());
            }
        }

        return ValidationResult.Success;
    }
}

[JsonSerializable(typeof(McpTransportConfiguration))]
public partial class McpTransportConfigurationJsonSerializerContext : JsonSerializerContext;