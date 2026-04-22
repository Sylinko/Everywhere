using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Interop;
using ZLinq;
#if DEBUG
using JetBrains.Profiler.Api;
#endif

namespace Everywhere.Views;

public partial class VisualTreeDebugger : UserControl
{
    private readonly IVisualElementContext _visualElementContext;
    private readonly IWindowHelper _windowHelper;
    private readonly ObservableCollection<IVisualElement> _rootElements = [];
    private readonly IReadOnlyList<VisualElementProperty> _properties = typeof(DebuggerVisualElement)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Select(p => new VisualElementProperty(p))
        .ToReadOnlyList();
    private readonly VisualElementOverlayWindow _treeViewPointerOverOverlayWindow;

    public VisualTreeDebugger(
        IShortcutListener shortcutListener,
        IVisualElementContext visualElementContext,
        IWindowHelper windowHelper)
    {
        _visualElementContext = visualElementContext;
        _windowHelper = windowHelper;

        InitializeComponent();

        VisualTreeView.ItemsSource = _rootElements;
        PropertyItemsControl.ItemsSource = _properties;

        shortcutListener.Register(
            new KeyboardShortcut(Key.C, KeyModifiers.Control | KeyModifiers.Shift),
            () =>
            {
                _rootElements.Clear();
                var element = visualElementContext.ElementFromPointer();
                if (element == null) return;
                element = element
                    .GetAncestors()
                    .LastOrDefault() ?? element;
                _rootElements.Add(element);
            });

        _treeViewPointerOverOverlayWindow = new VisualElementOverlayWindow
        {
            Content = new Border
            {
                Background = Brushes.DodgerBlue,
                Opacity = 0.2
            },
        };
    }

    private void HandleVisualTreeViewPointerMoved(object? sender, PointerEventArgs e)
    {
        IVisualElement? visualElement = null;
        var element = e.Source as StyledElement;
        while (element != null)
        {
            element = element.Parent;
            if (element != null && (visualElement = element.DataContext as IVisualElement) != null)
            {
                break;
            }
        }

        _treeViewPointerOverOverlayWindow.UpdateForVisualElement(visualElement);
    }

    private void HandleVisualTreeViewSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var debuggerElement = VisualTreeView.SelectedItem is not IVisualElement selectedItem ? null : new DebuggerVisualElement(selectedItem);
        foreach (var property in _properties)
        {
            property.Target = debuggerElement;
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.Title = nameof(VisualTreeDebugger);
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod
    // SetCloaked won't throw, so it's safe here.
    private async void HandlePickElementButtonClicked(object? sender, RoutedEventArgs e)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is not null) _windowHelper.SetCloaked(window, true);

        try
        {
            _rootElements.Clear();
            if (await _visualElementContext.PickVisualElementAsync(ScreenSelectionModes.Element) is { } element)
            {
                _rootElements.Add(element);
            }
        }
        catch
        {
            // ignored
        }

        if (window is not null) _windowHelper.SetCloaked(window, false);
    }

    private async void HandleCaptureButtonClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (VisualTreeView.SelectedItem is not IVisualElement selectedItem) return;

            using var pointer = await selectedItem.CaptureAsync(CancellationToken.None);
            var bitmap = pointer.ToAvaloniaBitmap();
#if DEBUG
            bitmap?.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.png"));
#endif
            CaptureImage.Source = bitmap;
        }
        catch (Exception ex)
        {
            CaptureImage.Source = null;
            Debug.WriteLine(ex);
        }
    }

    private async void HandleBuildButtonClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            const VisualContextDetailLevel level = VisualContextDetailLevel.Compact;
            var tokenLimit = int.Parse(TokenLimitTextBox.Text ?? "8000");
            var effectScope =
                ServiceLocator.Resolve<VisualElementEffect>().CreateScanEffect(CancellationToken.None);
            var builder = new VisualContextBuilder(
                VisualTreeView.SelectedItems.AsValueEnumerable().OfType<IVisualElement>().ToList(),
                tokenLimit,
                0,
                level,
                effectScope: effectScope);
#if DEBUG
            // use profiler to measure building time in debug mode
            var visualTree = await Task.Run(() =>
            {
                var originalThreadName = Thread.CurrentThread.Name;
                Thread.CurrentThread.Name = "Visual Tree Builder Thread";
                MeasureProfiler.StartCollectingData("BuildVisualTree");

                try
                {
                    return builder.Build(CancellationToken.None);
                }
                finally
                {
                    MeasureProfiler.SaveData("BuildVisualTree");
                    Thread.CurrentThread.Name = originalThreadName;
                }
            });
#else
            var visualTree = await Task.Run(() => builder.Build(CancellationToken.None));
#endif
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var extension = level switch
            {
                VisualContextDetailLevel.Compact => "json",
                VisualContextDetailLevel.Detailed => "xml",
                _ => "toon"
            };
            var filename = $"visual_tree_{timestamp}.{extension}";
            var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);
            await File.WriteAllTextAsync(filePath, visualTree);
            await App.Launcher.LaunchFileInfoAsync(new FileInfo(filePath));
        }
#if DEBUG
        catch (Exception ex)
        {
            _ = ex;
            Debugger.Break();
#else
        catch
        {
            // ignored
#endif
        }
    }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
internal class DebuggerVisualElement(IVisualElement element) : ObservableObject
{
    public string? Name { get; } = element.Name;

    public VisualElementType Type { get; } = element.Type;

    public VisualElementStates States => element.States;

    public IVisualElement? Parent { get; } = element.Parent;

    public int ProcessId { get; } = element.ProcessId;

    public string ProcessName
    {
        get
        {
            try
            {
                using var process = Process.GetProcessById(ProcessId);
                return process.ProcessName;
            }
            catch
            {
                return "Unknown";
            }
        }
    }

    public nint NativeWindowHandle { get; } = element.NativeWindowHandle;

    public PixelRect BoundingRectangle => element.BoundingRectangle;

    public string? Text => element.GetText();
}

internal class VisualElementProperty(PropertyInfo propertyInfo) : ObservableObject
{
    public DebuggerVisualElement? Target
    {
        get;
        set
        {
            if (field != null) field.PropertyChanged -= HandleElementPropertyChanged;
            field = value;
            if (field != null) field.PropertyChanged += HandleElementPropertyChanged;
            OnPropertyChanged(nameof(Value));
        }
    }

    private void HandleElementPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != propertyInfo.Name) return;
        OnPropertyChanged(nameof(Value));
    }

    public string Name => propertyInfo.Name;

    public bool IsReadOnly => !propertyInfo.CanWrite;

    public object? Value
    {
        get => Target == null ? null : propertyInfo.GetValue(Target);
        set
        {
            if (Target == null) return;
            if (IsReadOnly) return;
            propertyInfo.SetValue(Target, value);
        }
    }
}