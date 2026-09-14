using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Automation;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using Serilog;

namespace Everywhere.Views;

public partial class VisualTreeDebugger : UserControl, IDisposable
{
    private readonly IScreenSelectionService _screenSelectionService;
    private readonly IWindowHelper _windowHelper;
    private readonly DebuggerVisualContext _visualContext;
    private readonly ObservableCollection<DebuggerVisualElement> _rootElements = [];
    private readonly IReadOnlyList<VisualElementProperty> _properties =
    [
        .. typeof(DebuggerVisualElement)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.Name is not nameof(DebuggerVisualElement.Children))
            .Select(property => new VisualElementProperty(property)),
    ];
    private readonly VisualElementOverlayWindow _treeViewPointerOverOverlayWindow;
    private RemoteVisualAnchor? _anchor;

    public VisualTreeDebugger(
        IShortcutListener shortcutListener,
        IScreenSelectionService screenSelectionService,
        IWindowHelper windowHelper,
        DebuggerVisualContext visualContext)
    {
        _screenSelectionService = screenSelectionService;
        _windowHelper = windowHelper;
        _visualContext = visualContext;

        InitializeComponent();

        VisualTreeView.ItemsSource = _rootElements;
        PropertyItemsControl.ItemsSource = _properties;

        shortcutListener.Register(
            new KeyboardShortcut(Key.C, KeyModifiers.Control | KeyModifiers.Shift),
            () => Dispatcher.UIThread.PostOnDemand(() => PickPointerElementAsync().Detach(Log.Logger.ToExceptionHandler())));

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
        DebuggerVisualElement? target = null;
        var element = e.Source as StyledElement;
        while (element is not null)
        {
            element = element.Parent;
            if (element?.DataContext is not DebuggerVisualElement debuggerVisualElement) continue;
            target = debuggerVisualElement;
            break;
        }

        _treeViewPointerOverOverlayWindow.UpdateForRemoteVisualTarget(_visualContext, target?.TargetId);
    }

    private void HandleVisualTreeViewSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var debuggerElement = VisualTreeView.SelectedItem as DebuggerVisualElement;
        foreach (var property in _properties) property.Target = debuggerElement;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (TopLevel.GetTopLevel(this) is Window window) window.Title = nameof(VisualTreeDebugger);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        ResetElements();
        base.OnUnloaded(e);
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod
    // SetCloaked won't throw, so this event handler owns and reports the asynchronous operation.
    private async void HandlePickElementButtonClicked(object? sender, RoutedEventArgs e)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is not null) _windowHelper.SetCloaked(window, true);

        try
        {
            ResetElements();
            await _visualContext.BeginInspectionAsync();
            var anchor = await _screenSelectionService.PickVisualElementAsync(_visualContext, ScreenSelectionMode.Element);
            if (anchor is not null) await LoadAnchorAsync(anchor);
        }
        catch (Exception exception)
        {
            ResetElements();
            Log.Error(exception, "Failed to pick a visual element for VisualTreeDebugger.");
        }
        finally
        {
            if (window is not null) _windowHelper.SetCloaked(window, false);
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod
    private async void HandleCaptureButtonClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (VisualTreeView.SelectedItem is not DebuggerVisualElement selectedItem) return;

            using var capture = await _visualContext.CaptureTargetAsync(selectedItem.TargetId);
            var bitmap = capture.ToAvaloniaBitmap();
#if DEBUG
            bitmap?.Save(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.png"),
                PngBitmapEncoderOptions.Default);
#endif
            CaptureImage.Source = bitmap;
        }
        catch (Exception exception)
        {
            CaptureImage.Source = null;
            Log.Error(exception, "Failed to capture a VisualTreeDebugger target.");
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod
    private async void HandleBuildButtonClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var tokenLimit = int.Parse(TokenLimitTextBox.Text ?? "8000");
            var targetIds = VisualTreeView.SelectedItems
                .AsValueEnumerable()
                .OfType<DebuggerVisualElement>()
                .Select(static item => item.TargetId)
                .ToArray();
            if (targetIds.Length == 0) return;

            var filePath = await _visualContext.WriteVisualTreeFileAsync(targetIds, tokenLimit);
            await App.Launcher.LaunchFileInfoAsync(new FileInfo(filePath));
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to build a visual tree from VisualTreeDebugger targets.");
        }
    }

    /// <summary>Releases the current diagnostic anchor owned by this View.</summary>
    public void Dispose()
    {
        _anchor?.Dispose();
        _anchor = null;
    }

    private async Task PickPointerElementAsync()
    {
        ResetElements();
        await _visualContext.BeginInspectionAsync();
        var anchor = await _visualContext.AcquireAnchorAsync(VisualElementLocator.Pointer);
        if (anchor is not null) await LoadAnchorAsync(anchor);
    }

    private async Task LoadAnchorAsync(RemoteVisualAnchor anchor)
    {
        _anchor = anchor;
        try
        {
            var tree = await _visualContext.InspectAnchorAsync(anchor);
            foreach (var root in tree.Roots) _rootElements.Add(new DebuggerVisualElement(root));
        }
        catch
        {
            ResetElements();
            throw;
        }
    }

    private void ResetElements()
    {
        _treeViewPointerOverOverlayWindow.UpdateForRemoteVisualTarget(null, null);
        foreach (var property in _properties) property.Target = null;
        _rootElements.Clear();
        _anchor?.Dispose();
        _anchor = null;
    }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
internal sealed class DebuggerVisualElement(AutomationVisualTreeNode node) : ObservableObject
{
    public int TargetId => node.TargetId;

    public string? Id => node.Snapshot.Id;

    public string? Name => node.Snapshot.Name;

    public VisualElementType? Type => node.Snapshot.Type;

    public VisualElementStates? States => node.Snapshot.States;

    public int? ProcessId => node.Snapshot.ProcessId;

    public string ProcessName
    {
        get
        {
            try
            {
                if (ProcessId is not > 0) return "Unknown";
                using var process = Process.GetProcessById(ProcessId.Value);
                return process.ProcessName;
            }
            catch
            {
                return "Unknown";
            }
        }
    }

    public nint? NativeWindowHandle => node.Snapshot.NativeWindowHandle;

    public PixelRect? BoundingRectangle => node.Snapshot.Bounds;

    public string? Text => node.Snapshot.TextPreview;

    public VisualElementFields AvailableFields => node.Observation.AvailableFields;

    public VisualElementFields MissingFields => node.Observation.MissingFields;

    public string Status => string.Join(Environment.NewLine, node.Status);

    public IReadOnlyList<DebuggerVisualElement> Children => field ??= node.Children.Select(static child => new DebuggerVisualElement(child)).ToArray();
}

internal sealed class VisualElementProperty(PropertyInfo propertyInfo) : ObservableObject
{
    public DebuggerVisualElement? Target
    {
        get;
        set
        {
            if (field is not null) field.PropertyChanged -= HandleElementPropertyChanged;
            field = value;
            if (field is not null) field.PropertyChanged += HandleElementPropertyChanged;
            OnPropertyChanged(nameof(Value));
        }
    }

    public string Name => propertyInfo.Name;

    public bool IsReadOnly => !propertyInfo.CanWrite;

    public object? Value
    {
        get => Target is null ? null : propertyInfo.GetValue(Target);
        set
        {
            if (Target is null || IsReadOnly) return;
            propertyInfo.SetValue(Target, value);
        }
    }

    private void HandleElementPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == propertyInfo.Name) OnPropertyChanged(nameof(Value));
    }
}
