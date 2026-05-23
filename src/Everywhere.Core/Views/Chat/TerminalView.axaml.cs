using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Everywhere.Chat.Plugins;
using Serilog;
using TextMateSharp.Grammars;

namespace Everywhere.Views;

public sealed class TerminalView : TemplatedControl
{
    private sealed class StatusConverterImpl : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            // values: [DateTimeOffset? FinishedAt, int? ExitCode]
            if (values is not [DateTimeOffset, _])
                return null; // loading

            return values[1] is 0; // 0: success=true
        }
    }

    public static IMultiValueConverter StatusConverter { get; } = new StatusConverterImpl();

    /// <summary>
    /// Defines the <see cref="DisplayBlock"/> property.
    /// </summary>
    public static readonly StyledProperty<ChatPluginTerminalDisplayBlock?> DisplayBlockProperty =
        AvaloniaProperty.Register<TerminalView, ChatPluginTerminalDisplayBlock?>(nameof(DisplayBlock));

    /// <summary>
    /// Gets or sets the <see cref="ChatPluginTerminalDisplayBlock"/> that provides the terminal data and handles input.
    /// </summary>
    public ChatPluginTerminalDisplayBlock? DisplayBlock
    {
        get => GetValue(DisplayBlockProperty);
        set => SetValue(DisplayBlockProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="ColorTheme"/> property.
    /// </summary>
    public static readonly StyledProperty<ThemeName> ColorThemeProperty =
        AvaloniaProperty.Register<TerminalView, ThemeName>(nameof(ColorTheme));

    /// <summary>
    /// Gets or sets the color theme for the terminal. This property can be used to apply syntax highlighting themes to the terminal command & output.
    /// </summary>
    public ThemeName ColorTheme
    {
        get => GetValue(ColorThemeProperty);
        set => SetValue(ColorThemeProperty, value);
    }

    public TerminalView()
    {
        AddHandler(TextInputEvent, HandleTextInput, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
    }

    private async void HandleTextInput(object? sender, TextInputEventArgs e)
    {
        try
        {
            if (DisplayBlock is not { } block || string.IsNullOrEmpty(e.Text)) return;

            e.Handled = true;
            await block.WriteInputAsync(e.Text);
        }
        catch (Exception ex)
        {
            Log.ForContext<TerminalView>().Error(ex, "Error handling text input");
        }
    }

    private async void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            if (DisplayBlock is not { } block) return;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.C)
            {
                e.Handled = true;
                await block.WriteInputAsync("\x03");
                return;
            }

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.V ||
                e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.Insert)
            {
                e.Handled = true;
                await PasteFromClipboardAsync();
                return;
            }

            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    await block.WriteInputAsync("\r");
                    break;
                case Key.Back:
                    e.Handled = true;
                    await block.WriteInputAsync("\x7f");
                    break;
                case Key.Delete:
                    e.Handled = true;
                    await block.WriteInputAsync("\e[3~");
                    break;
                case Key.Left:
                    e.Handled = true;
                    await block.WriteInputAsync("\e[D");
                    break;
                case Key.Right:
                    e.Handled = true;
                    await block.WriteInputAsync("\e[C");
                    break;
                case Key.Up:
                    e.Handled = true;
                    await block.WriteInputAsync("\e[A");
                    break;
                case Key.Down:
                    e.Handled = true;
                    await block.WriteInputAsync("\e[B");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.ForContext<TerminalView>().Error(ex, "Error handling key input");
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        if (DisplayBlock is not { } block) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        string? text;
        try
        {
            text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrEmpty(text)) return;
        }
        catch
        {
            return;
        }

        await block.WritePasteAsync(text);
    }
}
