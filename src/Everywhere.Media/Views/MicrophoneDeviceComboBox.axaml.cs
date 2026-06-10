using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Collections;
using Everywhere.Media.Audio;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Media.Views;

[TemplatePart(Name = "PART_ComboBox", Type = typeof(ComboBox), IsRequired = true)]
public sealed partial class MicrophoneDeviceComboBox(IServiceProvider serviceProvider) : TemplatedControl
{
    public sealed record Item(string? Id, IDynamicResourceKey NameKey);

    public static readonly StyledProperty<string?> SelectedDeviceIdProperty =
        AvaloniaProperty.Register<MicrophoneDeviceComboBox, string?>(nameof(SelectedDeviceId));

    public string? SelectedDeviceId
    {
        get => GetValue(SelectedDeviceIdProperty);
        set => SetValue(SelectedDeviceIdProperty, value);
    }

    public IReadOnlyBindableList<Item> ItemsSource => _items;

    private readonly IMicrophoneDeviceManager _microphoneDeviceManager = serviceProvider.GetRequiredService<IMicrophoneDeviceManager>();
    private readonly BindableList<Item> _items = [];

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (!RefreshCommand.IsRunning)
        {
            RefreshCommand.Execute(null);
        }
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        var selectedDeviceId = SelectedDeviceId;

        _items.Clear();
        _items.Add(new Item(null, new DynamicResourceKey(LocaleKey.MicrophoneDeviceComboBox_DefaultDevice)));

        var hasSelectedDevice = selectedDeviceId is null;
        foreach (var device in _microphoneDeviceManager.GetInputDevices())
        {
            hasSelectedDevice |= string.Equals(device.Id, selectedDeviceId, StringComparison.Ordinal);
            _items.Add(new Item(device.Id, new DirectResourceKey(device.Name)));
        }

        if (!hasSelectedDevice && selectedDeviceId is { Length: > 0 })
        {
            _items.Add(
                new Item(
                    selectedDeviceId,
                    new FormattedDynamicResourceKey(
                        LocaleKey.MicrophoneDeviceComboBox_UnavailableDevice,
                        new DirectResourceKey(selectedDeviceId))));
        }

        return Task.CompletedTask;
    }
}