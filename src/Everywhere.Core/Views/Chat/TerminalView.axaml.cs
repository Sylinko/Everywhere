using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Everywhere.Chat.Plugins;
using Everywhere.Utilities;
using LiveMarkdown.Avalonia;
using Serilog;
using TextMateSharp.Grammars;

namespace Everywhere.Views;

[TemplatePart(Name = OutputCodeBlockPartName, Type = typeof(CodeBlock), IsRequired = true)]
public sealed class TerminalView : TemplatedControl
{
    private const string OutputCodeBlockPartName = "PART_OutputCodeBlock";

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

    private CodeBlock? _outputCodeBlock;
    private TerminalCodeBlockBridge? _outputBridge;

    public TerminalView()
    {
        AddHandler(TextInputEvent, HandleTextInput, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        DisposeCollector.DisposeToDefault(ref _outputBridge);
        _outputCodeBlock = e.NameScope.Find<CodeBlock>(OutputCodeBlockPartName);

        StartOutputBridge();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        StartOutputBridge();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        DisposeCollector.DisposeToDefault(ref _outputBridge);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DisplayBlockProperty)
        {
            DisposeCollector.DisposeToDefault(ref _outputBridge);
            _outputCodeBlock?.Inlines.Clear();
            StartOutputBridge();
        }
    }

    private void StartOutputBridge()
    {
        if (VisualRoot is null ||
            _outputBridge is not null ||
            _outputCodeBlock is null ||
            DisplayBlock is null)
        {
            return;
        }

        if (DisplayBlock.Run is not { } run)
        {
            _outputCodeBlock.IsVisible = false;
            return;
        }

        _outputCodeBlock.IsVisible = true;
        _outputBridge = new TerminalCodeBlockBridge(
            run,
            _outputCodeBlock,
            maxVisibleLines: 500);
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
