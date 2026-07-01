using System.Globalization;
using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Everywhere.Utilities;
using Serilog;

namespace Everywhere.Views;

[TemplatePart("PART_TextEditor", typeof(TextEditor), IsRequired = true)]
public class ChatTextEditor : TemplatedControl
{
    public static readonly DirectProperty<ChatTextEditor, string?> TextProperty =
        AvaloniaProperty.RegisterDirect<ChatTextEditor, string?>(
            nameof(Text),
            o => o.Text,
            (o, v) => o.Text = v);

    public string? Text
    {
        get => _text;
        set
        {
            if (_isTextChanging) return;
            if (string.Equals(_text, value, StringComparison.Ordinal)) return;

            _isTextChanging = true;
            try
            {
                var hasLeading = LeadingContent != null;
                _textEditor?.Text = hasLeading ? "\uFFFC" + value : value;
                SetAndRaise(TextProperty, ref _text, value);
            }
            finally
            {
                _isTextChanging = false;
            }
        }
    }

    public static readonly StyledProperty<int> MaxLengthProperty =
        TextBox.MaxLengthProperty.AddOwner<ChatTextEditor>();

    public int MaxLength
    {
        get => GetValue(MaxLengthProperty);
        set => SetValue(MaxLengthProperty, value);
    }

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<ChatTextEditor, string?>(nameof(Watermark));

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public string SelectedText
    {
        set => _textEditor?.SelectedText = value;
    }

    public static readonly StyledProperty<object?> LeadingContentProperty =
        AvaloniaProperty.Register<ChatTextEditor, object?>(nameof(LeadingContent));

    public object? LeadingContent
    {
        get => GetValue(LeadingContentProperty);
        set => SetValue(LeadingContentProperty, value);
    }

    public static readonly StyledProperty<IDataTemplate?> LeadingContentTemplateProperty =
        AvaloniaProperty.Register<ChatTextEditor, IDataTemplate?>(nameof(LeadingContentTemplate));

    public IDataTemplate? LeadingContentTemplate
    {
        get => GetValue(LeadingContentTemplateProperty);
        set => SetValue(LeadingContentTemplateProperty, value);
    }

    private IDisposable? _textChangedSubscription;
    private TextEditor? _textEditor;
    private bool _isTextChanging;
    private string? _text;

    public ChatTextEditor()
    {
        AddHandler(PreeditChangedEventRegistry.PreeditChangedEvent, HandlePreeditChanged, RoutingStrategies.Bubble);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        DisposeHelper.DisposeToDefault(ref _textChangedSubscription);

        if (_textEditor != null)
        {
            _textEditor.TextArea.TextView.ElementGenerators.Clear();
            _textEditor.TextArea.TextView.BackgroundRenderers.Clear();
        }

        _textEditor = e.NameScope.Find<TextEditor>("PART_TextEditor").NotNull();
        _textEditor.Text = _text;
        _textEditor.TextArea.TextView.ElementGenerators.Add(new LeadingContentElementGenerator(this, _textEditor));
        _textEditor.TextArea.TextView.BackgroundRenderers.Add(new WatermarkRenderer(this, _textEditor));

        _textEditor.TextChanged += HandleTextEditorTextChanged;
        _textChangedSubscription = Disposable.Create(() => _textEditor.TextChanged -= HandleTextEditorTextChanged);

        _textEditor.AddDisposableHandler(
            KeyDownEvent,
            HandleTextEditorKeyDownClipboard,
            RoutingStrategies.Tunnel);
    }

    private void HandleTextEditorTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextEditor textEditor) return;

        if (LeadingContent != null)
        {
            var document = textEditor.Document;
            if (document == null || document.TextLength == 0 || document.GetCharAt(0) != '\uFFFC')
            {
                LeadingContent = null;
                ClearValue(LeadingContentProperty);
            }
        }

        _isTextChanging = true;
        try
        {
            SetAndRaise(TextProperty, ref _text, textEditor.Text?.TrimStart('\uFFFC'));
            RaiseEvent(new TextChangedEventArgs(TextBox.TextChangedEvent, this)); // For ChatWindow Handling
        }
        finally
        {
            _isTextChanging = false;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LeadingContentProperty && _textEditor != null)
        {
            var hasLeading = change.NewValue != null;
            var document = _textEditor.Document;
            if (document == null) return;

            if (hasLeading)
            {
                if (document.TextLength == 0 || document.GetCharAt(0) != '\uFFFC')
                {
                    document.Insert(0, "\uFFFC");
                }
                else
                {
                    // \uFFFC is already present, just redraw visual lines to recreate the inline object with new context
                    _textEditor.TextArea.TextView.Redraw();
                }
            }
            else
            {
                if (document.TextLength > 0 && document.GetCharAt(0) == '\uFFFC')
                {
                    document.Remove(0, 1);
                }
            }
        }
        else if (change.Property == WatermarkProperty && _textEditor != null)
        {
            _textEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
    }

    public void Focus()
    {
        _textEditor?.TextArea.Focus();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _textEditor?.SelectionStart = _textEditor.Document.TextLength;
    }
    
    public async void Copy()
    {
        try
        {
            if (_textEditor is not { } textEditor) return;

            var clipboard = TopLevel.GetTopLevel(textEditor)?.Clipboard;
            if (clipboard is null) return;

            var selectionText = textEditor.SelectedText.TrimStart('\uFFFC');
            if (!string.IsNullOrEmpty(selectionText))
            {
                await clipboard.SetTextAsync(selectionText);
            }
        }
        catch (Exception ex)
        {
            Log.ForContext<ChatTextEditor>().Information(ex, "Failed to copy");
        }
    }

    public async void Cut()
    {
        try
        {
            if (_textEditor is not { } textEditor) return;

            var clipboard = TopLevel.GetTopLevel(textEditor)?.Clipboard;
            if (clipboard is null) return;

            var selectionText = textEditor.SelectedText.TrimStart('\uFFFC');
            var start = textEditor.SelectionStart;
            var length = textEditor.SelectionLength;
            textEditor.Document.Remove(start, length);

            if (!string.IsNullOrEmpty(selectionText))
            {
                await clipboard.SetTextAsync(selectionText);
            }
        }
        catch (Exception ex)
        {
            Log.ForContext<ChatTextEditor>().Information(ex, "Failed to cut");
        }
    }

    public async void Paste()
    {
        try
        {
            if (_textEditor is not { Document: { } document, TextArea: { } textArea } textEditor) return;

            var clipboard = TopLevel.GetTopLevel(textEditor)?.Clipboard;
            if (clipboard is null) return;

            var pastingEvent = new RoutedEventArgs(TextBox.PastingFromClipboardEvent, this);
            RaiseEvent(pastingEvent);
            if (pastingEvent.Handled) return;

            document.BeginUpdate();
            try
            {
                var text = await clipboard.TryGetTextAsync();
                if (text.IsNullOrEmpty()) return;

                text = text.Replace("\uFFFC", string.Empty);
                if (!string.IsNullOrEmpty(text))
                {
                    textArea.Selection.ReplaceSelectionWithText(text);
                }

                textArea.Caret.BringCaretToView();
            }
            catch (OutOfMemoryException) { } // May happen when pasting huge text
            finally
            {
                textArea.Document.EndUpdate();
            }
        }
        catch (Exception ex)
        {
            Log.ForContext<ChatTextEditor>().Information(ex, "Failed to paste");
        }
    }

    private static void HandleTextEditorKeyDownClipboard(object? sender, KeyEventArgs e)
    {
        if (sender is not TextEditor textEditor) return;

        switch (e.Key)
        {
            case Key.V when e.KeyModifiers.HasFlag(KeyModifiers.Control) && textEditor.CanPaste:
            {
                textEditor.Paste();
                e.Handled = true;
                break;
            }
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) && textEditor.CanCopy:
            {
                textEditor.Copy();
                e.Handled = true;
                break;
            }
            case Key.X when e.KeyModifiers.HasFlag(KeyModifiers.Control) && textEditor.CanCut:
            {
                textEditor.Cut();
                e.Handled = true;
                break;
            }
        }
    }

    // AvaloniaEdit does not support preedit, so this is a workaround for it.
    // I use harmony to patch TextAreaTextInputMethodClient, and raise PreeditChangedEvent when the preedit text changes.
    // Then I handle this event in ChatTextEditor and update the PreeditText and PreeditRect properties accordingly.
    // https://github.com/AvaloniaUI/AvaloniaEdit/pull/532

    #region Preedit

    public static readonly DirectProperty<ChatTextEditor, string?> PreeditTextProperty = AvaloniaProperty.RegisterDirect<ChatTextEditor, string?>(
        nameof(PreeditText),
        o => o.PreeditText);

    public static readonly DirectProperty<ChatTextEditor, Rect> PreeditRectProperty = AvaloniaProperty.RegisterDirect<ChatTextEditor, Rect>(
        nameof(PreeditRect),
        o => o.PreeditRect);

    public string? PreeditText
    {
        get;
        private set => SetAndRaise(PreeditTextProperty, ref field, value);
    }

    public Rect PreeditRect
    {
        get;
        private set => SetAndRaise(PreeditRectProperty, ref field, value);
    }

    private void HandlePreeditChanged(object? sender, PreeditChangedEventArgs e)
    {
        PreeditText = e.PreeditText;
        PreeditRect = e.CursorRectangle;
    }

    #endregion
}

file class LeadingContentElementGenerator(ChatTextEditor chatTextEditor, TextEditor textEditor) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (startOffset > 0) return -1;
        if (chatTextEditor.LeadingContent == null) return -1;
        var document = textEditor.Document;
        if (document is { TextLength: > 0 } && document.GetCharAt(0) == '\uFFFC') return 0;
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (offset != 0 || chatTextEditor.LeadingContent == null) return null;
        var document = textEditor.Document;
        if (document == null || document.TextLength == 0 || document.GetCharAt(0) != '\uFFFC') return null;

        var contentControl = new ContentControl
        {
            Content = chatTextEditor.LeadingContent,
            ContentTemplate = chatTextEditor.LeadingContentTemplate,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0) // Add some spacing between the leading content and the text
        };

        return new CenteredInlineObjectElement(1, contentControl);
    }
}

file class CenteredInlineObjectElement(int documentLength, Control element) : InlineObjectElement(documentLength, element)
{
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        return new CenteredInlineObjectRun(1, TextRunProperties, Element);
    }
}

file class CenteredInlineObjectRun(int length, TextRunProperties? properties, Control element)
    : InlineObjectRun(length, properties, element)
{
    public override double Baseline
    {
        get
        {
            var defaultBaseline = base.Baseline;
            if (!double.IsNaN(defaultBaseline) && Math.Abs(TextBlock.GetBaselineOffset(Element) - defaultBaseline) > 0.1)
            {
                return defaultBaseline; // Use explicit baseline if defined
            }

            var controlHeight = Size.Height;
            var fontSize = Properties?.FontRenderingEmSize ?? 14.0;
            // Center the control over the text baseline
            return controlHeight / 2 + (fontSize * 0.3);
        }
    }
}

file class WatermarkRenderer(ChatTextEditor chatTextEditor, TextEditor textEditor) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        var watermark = chatTextEditor.Watermark;
        if (string.IsNullOrEmpty(watermark)) return;

        var document = textEditor.Document;
        if (document == null) return;
        if ((document.TextLength == 1 && document.GetCharAt(0) == '\uFFFC') || document.TextLength == 0)
        {
            var typeface = new Typeface(textEditor.FontFamily, textEditor.FontStyle, textEditor.FontWeight, textEditor.FontStretch);
            var formattedText = new FormattedText(
                watermark,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                textEditor.FontSize,
                textEditor.Foreground
            );

            double x = 4;
            double y = 0;

            if (textView.VisualLinesValid && textView.VisualLines.Count > 0)
            {
                var line = textView.VisualLines[0];
                var displayPos = line.GetVisualPosition(document.TextLength, VisualYPosition.TextTop);
                x = displayPos.X;
                y = displayPos.Y;
            }

            using var _ = drawingContext.PushOpacity(0.5);
            drawingContext.DrawText(formattedText, new Point(x, y));
        }
    }
}