using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Automation;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using Serilog;

namespace Everywhere.Views;

/// <summary>
/// Edits the current platform's exclusion identities. Presentation state and bitmap ownership are UI-thread-affine.
/// </summary>
public sealed partial class TextSelectionApplicationsControl : UserControl
{
    public static readonly StyledProperty<string[]> ApplicationsProperty =
        AvaloniaProperty.Register<TextSelectionApplicationsControl, string[]>(nameof(Applications), []);

    public static readonly StyledProperty<IDynamicLocaleKey?> HeaderKeyProperty =
        AvaloniaProperty.Register<TextSelectionApplicationsControl, IDynamicLocaleKey?>(nameof(HeaderKey));

    public static readonly StyledProperty<IDynamicLocaleKey?> DescriptionKeyProperty =
        AvaloniaProperty.Register<TextSelectionApplicationsControl, IDynamicLocaleKey?>(nameof(DescriptionKey));

    public static readonly StyledProperty<bool> IsCompatibilityHintVisibleProperty =
        AvaloniaProperty.Register<TextSelectionApplicationsControl, bool>(nameof(IsCompatibilityHintVisible));

    /// <summary>
    /// Gets or sets the independently persisted application rules edited by this control.
    /// </summary>
    public string[] Applications
    {
        get => GetValue(ApplicationsProperty);
        set => SetValue(ApplicationsProperty, value);
    }

    /// <summary>
    /// Gets or sets the localized heading for the bound exclusion list.
    /// </summary>
    public IDynamicLocaleKey? HeaderKey
    {
        get => GetValue(HeaderKeyProperty);
        set => SetValue(HeaderKeyProperty, value);
    }

    /// <summary>
    /// Gets or sets the localized explanation of the bound rules.
    /// </summary>
    public IDynamicLocaleKey? DescriptionKey
    {
        get => GetValue(DescriptionKeyProperty);
        set => SetValue(DescriptionKeyProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the global capture compatibility hint is shown.
    /// </summary>
    public bool IsCompatibilityHintVisible
    {
        get => GetValue(IsCompatibilityHintVisibleProperty);
        set => SetValue(IsCompatibilityHintVisibleProperty, value);
    }

    public TextSelectionApplicationsPresentation Presentation { get; } = new();

    private readonly INativeHelper _nativeHelper;
    private readonly IScreenSelectionService _screenSelectionService;
    private readonly IWindowHelper _windowHelper;
    private readonly ChatVisualService _visualService;

    private CancellationTokenSource? _lifetime;

    public TextSelectionApplicationsControl(
        INativeHelper nativeHelper,
        IScreenSelectionService screenSelectionService,
        IWindowHelper windowHelper,
        ChatVisualService visualService)
    {
        _nativeHelper = nativeHelper;
        _screenSelectionService = screenSelectionService;
        _windowHelper = windowHelper;
        _visualService = visualService;
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _lifetime = new CancellationTokenSource();
        ReconcileRows();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;

        foreach (var row in Presentation.Rows)
        {
            row.Dispose();
        }

        Presentation.Rows.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ApplicationsProperty && _lifetime is not null)
        {
            ReconcileRows();
        }
    }

    private void ReconcileRows()
    {
        var identities = Applications
            .AsValueEnumerable()
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Distinct(IdentityComparer)
            .ToArray();

        for (var index = Presentation.Rows.Count - 1; index >= 0; index--)
        {
            var row = Presentation.Rows[index];
            if (identities.Contains(row.Identity, IdentityComparer))
            {
                continue;
            }

            Presentation.Rows.RemoveAt(index);
            row.Dispose();
        }

        for (var index = 0; index < identities.Length; index++)
        {
            var identity = identities[index];
            var row = Presentation.Rows.AsValueEnumerable().FirstOrDefault(item => IdentityComparer.Equals(item.Identity, identity));
            if (row is null)
            {
                row = new TextSelectionApplicationRow(identity);
                Presentation.Rows.Insert(index, row);
                LoadApplicationAsync(row, _lifetime?.Token ?? CancellationToken.None).Detach(Log.Logger.ToExceptionHandler());
            }
            else
            {
                var currentIndex = Presentation.Rows.IndexOf(row);
                if (currentIndex != index)
                {
                    Presentation.Rows.Move(currentIndex, index);
                }
            }
        }

        Presentation.IsEmpty = Presentation.Rows.Count == 0;
    }

    private async Task LoadApplicationAsync(TextSelectionApplicationRow row, CancellationToken cancellationToken)
    {
        NativeApplicationInfo? application = null;
        Bitmap? icon = null;
        try
        {
            // AppKit image representations are accessed on the main thread; Windows shell extraction may block.
#if WINDOWS || LINUX
            await Task.Run(
                () =>
                {
                    application = _nativeHelper.GetApplicationInfo(row.Identity);
                    icon = _nativeHelper.GetApplicationIcon(application?.Path);
                },
                cancellationToken);
#elif MACOS
            await Task.CompletedTask;
            application = _nativeHelper.GetApplicationInfo(row.Identity);
            icon = _nativeHelper.GetApplicationIcon(application?.Path);
#else
#error Implement application metadata and icon loading for this platform.
#endif
            if (cancellationToken.IsCancellationRequested || !Presentation.Rows.Contains(row))
            {
                return;
            }

            if (application is not null)
            {
                row.DisplayName = application.DisplayName;
            }

            row.Icon = icon;
            icon = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Could not load the application metadata for {Identity}", row.Identity);
        }
        finally
        {
            icon?.Dispose();
        }
    }

    private void HandleChooseApplication(object? sender, RoutedEventArgs e)
    {
        RunSelectionAsync(false).Detach(Log.Logger.ToExceptionHandler());
    }

    private void HandlePickWindow(object? sender, RoutedEventArgs e)
    {
        RunSelectionAsync(true).Detach(Log.Logger.ToExceptionHandler());
    }

    private async Task RunSelectionAsync(bool shouldPickWindow)
    {
        if (_lifetime is null || Presentation.IsBusy)
        {
            return;
        }

        var cancellationToken = _lifetime.Token;
        Presentation.IsBusy = true;
        Presentation.ErrorKey = null;
        Presentation.IsEditing = false;
        var window = TopLevel.GetTopLevel(this) as Window;
        var isCloaked = false;

        try
        {
            NativeApplicationInfo? application;
            if (shouldPickWindow)
            {
                if (window is not null)
                {
                    _windowHelper.SetCloaked(window, true);
                    isCloaked = true;
                }

                using var anchor = await _screenSelectionService.PickApplicationWindowAsync(
                    _visualService.AcquisitionContext,
                    cancellationToken);
                if (anchor is null)
                {
                    return;
                }

                var snapshot = await _visualService.AcquisitionContext.GetElementSnapshotAsync(
                    anchor,
                    VisualElementFields.ProcessId,
                    cancellationToken: cancellationToken);
                application = snapshot.ProcessId is { } processId ? _nativeHelper.GetApplicationInfo(processId) : null;
            }
            else
            {
                var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? App.StorageProvider;
                var files = await storage.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = LocaleKey.TextSelectionApplications_ChooseApplication.I18N(),
                        AllowMultiple = false,
                        FileTypeFilter =
                        [
                            new FilePickerFileType(LocaleKey.TextSelectionApplications_ApplicationFile.I18N())
                            {
#if WINDOWS
                                Patterns = ["*.exe"],
#elif MACOS
                                Patterns = ["*.app"],
                                AppleUniformTypeIdentifiers = ["com.apple.application-bundle"],
#elif LINUX
                                Patterns = ["*.desktop", "*"],
#else
#error Implement the application file picker for this platform.
#endif
                            },
                        ],
                    });

                if (files.Count == 0)
                {
                    return;
                }

                var path = files[0].TryGetLocalPath();
                application = path is not null && Path.IsPathFullyQualified(path) ? _nativeHelper.GetApplicationInfo(path) : null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (application is null)
            {
                Presentation.ErrorKey = new DynamicLocaleKey(LocaleKey.TextSelectionApplications_ResolveFailed);
                return;
            }

            AddApplication(application.Identity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not select an application for text-selection exclusions.");
            Presentation.ErrorKey = new DynamicLocaleKey(LocaleKey.TextSelectionApplications_ResolveFailed);
        }
        finally
        {
            if (isCloaked && window is not null)
            {
                _windowHelper.SetCloaked(window, false);
            }

            Presentation.IsBusy = false;
        }
    }

    private void HandleManualEntry(object? sender, RoutedEventArgs e)
    {
        Presentation.ResetManualInput();
        Presentation.ErrorKey = null;
        Presentation.IsEditing = true;
    }

    private void HandleConfirm(object? sender, RoutedEventArgs e)
    {
        if (!Presentation.Validate())
        {
            return;
        }

        AddApplication(Presentation.ManualInput.Trim());
    }

    private void AddApplication(string identity)
    {
        if (Applications.Contains(identity, IdentityComparer))
        {
            Presentation.ErrorKey = new DynamicLocaleKey(LocaleKey.TextSelectionApplications_Duplicate);
            return;
        }

        SetCurrentValue(ApplicationsProperty, [.. Applications, identity]);
        Presentation.IsEditing = false;
        Presentation.ErrorKey = null;
    }

    private void HandleCancel(object? sender, RoutedEventArgs e)
    {
        Presentation.IsEditing = false;
        Presentation.ErrorKey = null;
    }

    private void HandleEditorKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HandleCancel(sender, e);
                break;
            case Key.Enter:
                HandleConfirm(sender, e);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void HandleRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextSelectionApplicationRow row })
        {
            return;
        }

        var applications = Applications
            .AsValueEnumerable()
            .Where(identity => !IdentityComparer.Equals(identity, row.Identity))
            .ToArray();
        SetCurrentValue(ApplicationsProperty, applications);
    }

    private static StringComparer IdentityComparer
    {
        get
        {
#if MACOS || LINUX
            return StringComparer.Ordinal;
#elif WINDOWS
            return StringComparer.OrdinalIgnoreCase;
#else
#error Define application identity comparison for this platform.
#endif
        }
    }
}

/// <summary>
/// Owns the small amount of UI state for the exclusion editor.
/// </summary>
public sealed partial class TextSelectionApplicationsPresentation : ObservableValidator
{
    public ObservableCollection<TextSelectionApplicationRow> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(TextSelectionApplicationsPresentation), nameof(ValidateManualIdentity))]
    public partial string ManualInput { get; set; } = string.Empty;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial IDynamicLocaleKey? ErrorKey { get; set; }

    public bool HasError => ErrorKey is not null;

#if MACOS
    public IDynamicLocaleKey ManualLabel { get; } = new DynamicLocaleKey(LocaleKey.TextSelectionApplications_AddBundleId);

    public IDynamicLocaleKey ManualHint { get; } = new DynamicLocaleKey(LocaleKey.TextSelectionApplications_BundleIdHint);

    public IDynamicLocaleKey ManualPlaceholder { get; } = new DirectLocaleKey("com.apple.TextEdit");
#elif WINDOWS
    public IDynamicLocaleKey ManualLabel { get; } = new DynamicLocaleKey(LocaleKey.TextSelectionApplications_AddProcessName);

    public IDynamicLocaleKey ManualHint { get; } = new DynamicLocaleKey(LocaleKey.TextSelectionApplications_ProcessNameHint);

    public IDynamicLocaleKey ManualPlaceholder { get; } = new DirectLocaleKey("Code.exe");
#elif LINUX
    public IDynamicLocaleKey ManualLabel { get; } = new DynamicLocaleKey(LocaleKey.TextSelectionApplications_AddApplication);

    public IDynamicLocaleKey ManualHint { get; } = new DynamicLocaleKey(LocaleKey.TextSelectionApplications_ExecutablePathHint);

    public IDynamicLocaleKey ManualPlaceholder { get; } = new DirectLocaleKey("/usr/bin/ghostty");
#else
#error Define the manual application identity editor for this platform.
#endif

    /// <summary>
    /// Validates the current manual entry, including an untouched empty input.
    /// </summary>
    public bool Validate()
    {
        ValidateAllProperties();
        return !HasErrors;
    }

    /// <summary>
    /// Starts a fresh manual entry without showing an error before the user edits it.
    /// </summary>
    public void ResetManualInput()
    {
        ManualInput = string.Empty;
        ClearErrors(nameof(ManualInput));
    }

    /// <summary>
    /// Validates a manual platform identity for the TextBox data-validation binding.
    /// </summary>
    public static ValidationResult? ValidateManualIdentity(string? value)
    {
        var identity = value?.Trim() ?? string.Empty;
#if MACOS
        var isValid = identity.Length > 0 && identity.Split('.').All(part =>
            part.Length > 0 && part.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
#elif WINDOWS
        var isValid = identity.Length > 4 && identity.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            identity.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0 && !identity.Any(char.IsControl);
#elif LINUX
        var isValid = Path.IsPathFullyQualified(identity) && !identity.Any(char.IsControl);
#else
#error Implement manual application identity validation for this platform.
#endif
#if LINUX
        var errorKey = LocaleKey.TextSelectionApplications_InvalidExecutablePath;
#else
        var errorKey = LocaleKey.TextSelectionApplications_InvalidIdentity;
#endif
        return isValid ? ValidationResult.Success : new ValidationResult(errorKey.I18N());
    }
}

/// <summary>
/// Keeps row identity stable while application metadata loads. Owns its decoded icon.
/// </summary>
public sealed partial class TextSelectionApplicationRow : ObservableObject, IDisposable
{
    public string Identity { get; }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    [ObservableProperty]
    public partial Bitmap? Icon { get; set; }

    public TextSelectionApplicationRow(string identity)
    {
        Identity = identity;
        DisplayName = identity;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Icon?.Dispose();
        Icon = null;
    }
}