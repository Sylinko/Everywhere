using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Everywhere.AI;

namespace Everywhere.Views;

/// <summary>
/// A TextBox control for previewing the final endpoint URL of a ModelProviderSchema.
/// </summary>
[TemplatePart(Name = TextBoxPartName, Type = typeof(TextBox), IsRequired = true)]
public class PreviewEndpointTextBox : TemplatedControl
{
    private const string TextBoxPartName = "PART_TextBox";

    private TextBox? _textBox;

    /// <summary>
    /// Defines the <see cref="Endpoint"/> property
    /// </summary>
    public static readonly StyledProperty<string?> EndpointProperty =
        AvaloniaProperty.Register<PreviewEndpointTextBox, string?>(
            nameof(Endpoint),
            defaultBindingMode: BindingMode.TwoWay,
            enableDataValidation: true);

    /// <summary>
    /// The endpoint template to preview.
    /// </summary>
    public string? Endpoint
    {
        get => GetValue(EndpointProperty);
        set => SetValue(EndpointProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="Schema"/> property
    /// </summary>
    public static readonly StyledProperty<ModelProviderSchema> SchemaProperty =
        AvaloniaProperty.Register<PreviewEndpointTextBox, ModelProviderSchema>(nameof(Schema));

    /// <summary>
    /// The schema used to generate the preview endpoint.
    /// </summary>
    public ModelProviderSchema Schema
    {
        get => GetValue(SchemaProperty);
        set => SetValue(SchemaProperty, value);
    }

    /// <summary>
    /// Defines the read-only <see cref="PreviewEndpoint"/> property.
    /// </summary>
    public static readonly DirectProperty<PreviewEndpointTextBox, string?> PreviewEndpointProperty =
        AvaloniaProperty.RegisterDirect<PreviewEndpointTextBox, string?>(
            nameof(PreviewEndpoint),
            o => o.PreviewEndpoint);

    /// <summary>
    /// The generated preview endpoint based on the current <see cref="Endpoint"/> and <see cref="Schema"/>.
    /// </summary>
    public string? PreviewEndpoint
    {
        get;
        private set => SetAndRaise(PreviewEndpointProperty, ref field, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != SchemaProperty && change.Property != EndpointProperty) return;

        PreviewEndpoint = Schema.PreviewEndpoint(Endpoint);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _textBox = e.NameScope.Find<TextBox>(TextBoxPartName);
    }

    protected override void UpdateDataValidation(
        AvaloniaProperty property,
        BindingValueType state,
        Exception? error)
    {
        if (_textBox is not null && property == EndpointProperty)
        {
            DataValidationErrors.SetError(_textBox, error);
        }
    }
}