using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using ShadUI;

namespace Everywhere.Views;

[TemplatePart("PART_PrimaryButton", typeof(Button))]
[TemplatePart("PART_CancelButton", typeof(Button))]
public class McpTransportConfigurationForm : TemplatedControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<McpTransportConfigurationForm, string?>(nameof(Title));

    public static readonly StyledProperty<bool> IsVerifyingProperty =
        AvaloniaProperty.Register<McpTransportConfigurationForm, bool>(nameof(IsVerifying));

    public static readonly StyledProperty<IDynamicLocaleKey?> VerificationStatusKeyProperty =
        AvaloniaProperty.Register<McpTransportConfigurationForm, IDynamicLocaleKey?>(nameof(VerificationStatusKey));

    public static readonly DirectProperty<McpTransportConfigurationForm, McpTransportConfiguration> ConfigurationProperty =
        AvaloniaProperty.RegisterDirect<McpTransportConfigurationForm, McpTransportConfiguration>(
            nameof(Configuration),
            o => o.Configuration,
            (o, v) => o.Configuration = v);

    public static readonly DirectProperty<McpTransportConfigurationForm, int> SelectedTabIndexProperty =
        AvaloniaProperty.RegisterDirect<McpTransportConfigurationForm, int>(
            nameof(SelectedTabIndex),
            o => o.SelectedTabIndex,
            (o, v) => o.SelectedTabIndex = v);

    public static readonly DirectProperty<McpTransportConfigurationForm, StdioMcpTransportConfiguration> StdioConfigurationProperty =
        AvaloniaProperty.RegisterDirect<McpTransportConfigurationForm, StdioMcpTransportConfiguration>(
            nameof(StdioConfiguration),
            o => o.StdioConfiguration);

    public static readonly DirectProperty<McpTransportConfigurationForm, HttpMcpTransportConfiguration> HttpConfigurationProperty =
        AvaloniaProperty.RegisterDirect<McpTransportConfigurationForm, HttpMcpTransportConfiguration>(
            nameof(HttpConfiguration),
            o => o.HttpConfiguration);

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool IsVerifying
    {
        get => GetValue(IsVerifyingProperty);
        private set => SetValue(IsVerifyingProperty, value);
    }

    public IDynamicLocaleKey? VerificationStatusKey
    {
        get => GetValue(VerificationStatusKeyProperty);
        private set => SetValue(VerificationStatusKeyProperty, value);
    }

    /// <summary>
    /// Gets or sets an asynchronous verification and save handler invoked when the user confirms valid input.
    /// </summary>
    public Func<McpTransportConfiguration, IProgress<IDynamicLocaleKey>, CancellationToken, Task>? VerifyAndApplyAsync { get; set; }

    public McpTransportConfiguration Configuration
    {
        get => _configuration;
        set
        {
            if (!SetAndRaise(ConfigurationProperty, ref _configuration, value)) return;

            switch (value)
            {
                case StdioMcpTransportConfiguration stdioConfig:
                {
                    var oldConfig = StdioConfiguration;
                    StdioConfiguration = stdioConfig;
                    SelectedTabIndex = 0;
                    RaisePropertyChanged(StdioConfigurationProperty, oldConfig, StdioConfiguration);
                    break;
                }
                case HttpMcpTransportConfiguration sseConfig:
                {
                    var oldConfig = HttpConfiguration;
                    HttpConfiguration = sseConfig;
                    SelectedTabIndex = 1;
                    RaisePropertyChanged(HttpConfigurationProperty, oldConfig, HttpConfiguration);
                    break;
                }
            }
        }
    }

    public int SelectedTabIndex
    {
        get => _configuration == StdioConfiguration ? 0 : 1;
        set
        {
            var oldValue = _configuration;
            _configuration = value == 0 ? StdioConfiguration : HttpConfiguration;
            _configuration.Name = oldValue.Name;
            _configuration.Description = oldValue.Description;
            RaisePropertyChanged(ConfigurationProperty, oldValue, _configuration);
        }
    }

    public StdioMcpTransportConfiguration StdioConfiguration { get; private set; }

    public HttpMcpTransportConfiguration HttpConfiguration { get; private set; }

    private McpTransportConfiguration _configuration;
    private Button? _primaryButton;
    private Button? _cancelButton;
    private CancellationTokenSource? _verifyCts;

    public McpTransportConfigurationForm()
    {
        _configuration = StdioConfiguration = new StdioMcpTransportConfiguration();
        HttpConfiguration = new HttpMcpTransportConfiguration();
    }

    public async void BrowseWorkingDirectory()
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
        StdioConfiguration.WorkingDirectory = result.FirstOrDefault()?.Path.LocalPath;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_primaryButton is not null) _primaryButton.Click -= HandlePrimaryClick;
        if (_cancelButton is not null) _cancelButton.Click -= HandleCancelClick;

        base.OnApplyTemplate(e);
        _primaryButton = e.NameScope.Find<Button>("PART_PrimaryButton");
        _cancelButton = e.NameScope.Find<Button>("PART_CancelButton");

        if (_primaryButton is not null) _primaryButton.Click += HandlePrimaryClick;
        if (_cancelButton is not null) _cancelButton.Click += HandleCancelClick;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelActiveVerification();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape) return;

        e.Handled = true;
        CancelAndClose();
    }

    private void HandlePrimaryClick(object? sender, RoutedEventArgs e)
    {
        SubmitAsync().Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    private void HandleCancelClick(object? sender, RoutedEventArgs e)
    {
        CancelAndClose();
    }

    private async Task SubmitAsync()
    {
        if (IsVerifying || !Configuration.Validate()) return;

        if (VerifyAndApplyAsync is not { } verifyAndApply)
        {
            CloseDialog(DialogResult.Primary);
            return;
        }

        CancelActiveVerification();
        var cts = _verifyCts = new CancellationTokenSource();
        var verificationToken = cts.Token;
        IsVerifying = true;
        VerificationStatusKey = new DynamicLocaleKey(LocaleKey.McpTransportConfigurationForm_VerifyingStatus_Text);

        try
        {
            // Progress<IDynamicLocaleKey> captures AvaloniaSynchronizationContext on the UI thread,
            // marshaling background MCP startup updates directly to VerificationStatusKey.
            var progress = new Progress<IDynamicLocaleKey>(statusKey =>
            {
                // ReSharper disable once AccessToDisposedClosure
                if (!verificationToken.IsCancellationRequested && ReferenceEquals(_verifyCts, cts))
                {
                    VerificationStatusKey = statusKey;
                }
            });

            await verifyAndApply(Configuration, progress, verificationToken);

            if (!verificationToken.IsCancellationRequested)
            {
                CloseDialog(DialogResult.Primary);
            }
        }
        catch (OperationCanceledException) when (verificationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_verifyCts, cts))
            {
                _verifyCts = null;
                IsVerifying = false;
                VerificationStatusKey = null;
            }

            cts.Dispose();
        }
    }

    private void CancelAndClose()
    {
        CancelActiveVerification();
        IsVerifying = false;
        VerificationStatusKey = null;
        CloseDialog(DialogResult.Cancel);
    }

    private void CancelActiveVerification()
    {
        var cts = _verifyCts;
        _verifyCts = null;
        if (cts is null) return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CloseDialog(DialogResult result)
    {
        var host = this.FindAncestorOfType<DialogHost>() ?? DialogManager.ResolveHost(TopLevel.GetTopLevel(this));
        host?.Close(this, result);
    }
}
