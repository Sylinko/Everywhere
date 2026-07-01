using Avalonia.Controls.Primitives;
using Everywhere.Configuration;

namespace Everywhere.Views;

public class CreateApiKeyForm : TemplatedControl
{
    public static readonly StyledProperty<ApiKey> ApiKeyProperty =
        AvaloniaProperty.Register<CreateApiKeyForm, ApiKey>(nameof(ApiKey));

    public ApiKey ApiKey
    {
        get => GetValue(ApiKeyProperty);
        set => SetValue(ApiKeyProperty, value);
    }

    public CreateApiKeyForm(string? defaultName)
    {
        ApiKey = new ApiKey
        {
            Name = defaultName ?? string.Empty
        };
    }
}