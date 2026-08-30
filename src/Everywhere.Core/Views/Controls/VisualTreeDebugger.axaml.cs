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
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Interop;

namespace Everywhere.Views;

public partial class VisualTreeDebugger : UserControl
{
    private readonly IScreenSelectionService _screenSelectionService;
    private readonly IWindowHelper _windowHelper;
    private readonly VisualContext _visualContext;
    private readonly ObservableCollection<DebuggerVisualElement> _rootElements = [];
    private readonly IReadOnlyList<VisualElementProperty> _properties =
    [
        .. typeof(DebuggerVisualElement)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.Name is not nameof(DebuggerVisualElement.Element) and not nameof(DebuggerVisualElement.Children))
            .Select(property => new VisualElementProperty(property)),
    ];
    private readonly VisualElementOverlayWindow _treeViewPointerOverOverlayWindow;
    private VisualElementRetention _retention;

    public VisualTreeDebugger(IShortcutListener shortcutListener, IScreenSelectionService screenSelectionService, IWindowHelper windowHelper, IVisualElementBackend visualElementBackend)
    {
        _screenSelectionService = screenSelectionService;
        _windowHelper = windowHelper;
        _visualContext = new VisualContext();
        _retention = _visualContext.CreateRetention();

        InitializeComponent();

        VisualTreeView.ItemsSource = _rootElements;
        PropertyItemsControl.ItemsSource = _properties;

        shortcutListener.Register(
            new KeyboardShortcut(Key.C, KeyModifiers.Control | KeyModifiers.Shift),
            () => Dispatcher.UIThread.PostOnDemand(() =>
            {
                ResetElements();
                var result = visualElementBackend.Query(_retention, VisualElementLocator.Pointer);
                if (result is null) return;
                _rootElements.Add(new DebuggerVisualElement(GetRootElement(result), _retention));
            }));

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
        VisualElement? visualElement = null;
        var element = e.Source as StyledElement;
        while (element != null)
        {
            element = element.Parent;
            if (element?.DataContext is DebuggerVisualElement debuggerVisualElement)
            {
                visualElement = debuggerVisualElement.Element;
                break;
            }
        }

        _treeViewPointerOverOverlayWindow.UpdateForVisualElement(visualElement);
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

    // ReSharper disable once AsyncVoidEventHandlerMethod
    // SetCloaked won't throw, so it's safe here.
    private async void HandlePickElementButtonClicked(object? sender, RoutedEventArgs e)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is not null) _windowHelper.SetCloaked(window, true);

        try
        {
            ResetElements();
            if (await _screenSelectionService.PickVisualElementAsync(_retention, ScreenSelectionMode.Element) is { } result)
            {
                _rootElements.Add(new DebuggerVisualElement(result, _retention));
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            if (window is not null) _windowHelper.SetCloaked(window, false);
        }
    }

    private async void HandleCaptureButtonClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (VisualTreeView.SelectedItem is not DebuggerVisualElement selectedItem) return;

            using var pointer = await selectedItem.Element.CaptureAsync(CancellationToken.None);
            var bitmap = pointer.ToAvaloniaBitmap();
#if DEBUG
            bitmap?.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.png"), PngBitmapEncoderOptions.Default);
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
            var selectedElements = VisualTreeView.SelectedItems.AsValueEnumerable().OfType<DebuggerVisualElement>().Select(item => item.Element).ToArray();
            if (selectedElements.Length == 0) return;

            using var targetTurn = _visualContext.BeginTurn();
            using var traversalRetention = _visualContext.CreateRetention();
            var targetPublication = _visualContext.BeginPublication();
            using var effectScope = ServiceLocator.Resolve<VisualElementEffect>().CreateScanEffect(_visualContext, CancellationToken.None);
            var builder = new VisualContextBuilder(selectedElements, traversalRetention, targetPublication, tokenLimit, level, effectScope: effectScope);
            var visualTree = await Task.Run(() => builder.Build(CancellationToken.None));
            effectScope.Complete();
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var extension = level switch
            {
                VisualContextDetailLevel.Compact => "json",
                VisualContextDetailLevel.Detailed => "xml",
                _ => "toon"
            };
            var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"visual_tree_{timestamp}.{extension}");
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

    private VisualElementQueryResult GetRootElement(VisualElementQueryResult result)
    {
        var current = result;
        while (true)
        {
            using var parents = current.Element.CreateEnumerator(VisualElementRelation.Parent, VisualElementEnumerationOptions.Default);
            if (!parents.MoveNext()) return current;

            _retention.Retain(parents.Current.Element);
            current = parents.Current;
        }
    }

    private void ResetElements()
    {
        _rootElements.Clear();
        _retention.Dispose();
        _retention = _visualContext.CreateRetention();
    }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
internal sealed class DebuggerVisualElement : ObservableObject
{
    public VisualElement Element => _queryResult.Element;

    public string Id => Element.Id;

    public string? Name => _queryResult.Snapshot.Name;

    public VisualElementType? Type => _queryResult.Snapshot.Type;

    public VisualElementStates? States => _queryResult.Snapshot.States;

    public int? ProcessId => _queryResult.Snapshot.ProcessId;

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

    public nint? NativeWindowHandle => _queryResult.Snapshot.NativeWindowHandle;

    public PixelRect? BoundingRectangle => _queryResult.Snapshot.Bounds;

    public string? Text => _queryResult.Snapshot.TextPreview;

    public IReadOnlyList<DebuggerVisualElement> Children => _children ??= LoadChildren();

    private readonly VisualElementQueryResult _queryResult;
    private readonly VisualElementRetention _retention;
    private IReadOnlyList<DebuggerVisualElement>? _children;

    public DebuggerVisualElement(VisualElementQueryResult queryResult, VisualElementRetention retention)
    {
        _queryResult = queryResult;
        _retention = retention;
    }

    private IReadOnlyList<DebuggerVisualElement> LoadChildren()
    {
        var children = new List<DebuggerVisualElement>();
        using var enumerator = Element.CreateEnumerator(VisualElementRelation.Child, VisualElementEnumerationOptions.Default);
        while (enumerator.MoveNext())
        {
            _retention.Retain(enumerator.Current.Element);
            children.Add(new DebuggerVisualElement(enumerator.Current, _retention));
        }

        return children;
    }
}

internal sealed class VisualElementProperty(PropertyInfo propertyInfo) : ObservableObject
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

    public string Name => propertyInfo.Name;

    public bool IsReadOnly => !propertyInfo.CanWrite;

    public object? Value
    {
        get => Target == null ? null : propertyInfo.GetValue(Target);
        set
        {
            if (Target == null || IsReadOnly) return;
            propertyInfo.SetValue(Target, value);
        }
    }

    private void HandleElementPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == propertyInfo.Name) OnPropertyChanged(nameof(Value));
    }
}
