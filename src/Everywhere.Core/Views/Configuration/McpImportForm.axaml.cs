using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Serilog;
using ShadUI;
using TextMateSharp.Grammars;

namespace Everywhere.Views;

[TemplatePart(TextEditorPartName, typeof(TextEditor), IsRequired = true)]
public partial class McpImportForm : TemplatedControl
{
    private const string TextEditorPartName = "PART_TextEditor";

    public const string McpJsonWatermark =
        """
        // Single MCP Server
        {
          "type": "sse",
          "url": "http://localhost:3000/events",
          "note": "For SSE connections, add this URL directly in Client"
        }
        
        // Multiple MCP Servers
        {
          "mcpServers": {
            // Stdio MCP Example
            "stdio-mcp-example": {
              "command": "npx",
              "args": ["-y", "stdio-mcp-example"]
            },
            // SSE
            "sse-mcp-example": {
              "type": "sse",
              "url": "http://localhost:1145"
            }
        }
        """;

    public string? McpJson
    {
        get => _textEditor?.Text;
        set => _textEditor?.Text = value;
    }

    private TextEditor? _textEditor;

    [RelayCommand]
    private async Task OpenMcpJsonFileAsync()
    {
        try
        {
            var files = await App.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    FileTypeFilter =
                    [
                        new FilePickerFileType(LocaleResolver.FilePickerFileType_SupportedFiles)
                        {
                            Patterns = ["*.json"]
                        },
                        new FilePickerFileType(LocaleResolver.FilePickerFileType_AllFiles)
                        {
                            Patterns = ["*"]
                        }
                    ]
                });
            if (files.Count == 0) return;

            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            McpJson = await reader.ReadToEndAsync();
        }
        catch (Exception e)
        {
            e = HandledSystemException.Handle(e);
            ToastManager.Error(LocaleResolver.Common_Error, e.GetFriendlyMessage());
            Log.ForContext<McpImportForm>().Error(e, "Failed to open MCP JSON file");
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _textEditor = e.NameScope.Find<TextEditor>(TextEditorPartName);
        var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        var textMateInstallation = _textEditor.InstallTextMate(registryOptions);

        textMateInstallation.SetGrammar(registryOptions.GetScopeByLanguageId(registryOptions.GetLanguageByExtension(".json").Id));
    }
}