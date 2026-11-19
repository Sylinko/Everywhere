using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Everywhere.Views;

namespace Everywhere.Core.Tests.Modding;

/// <summary>
/// Mounts settings controls with their production templates instead of substituting a test editor.
/// </summary>
internal sealed class SettingsEditorTestWindow : Window
{
    public SettingsEditorTestWindow(SettingsItemsControl content)
    {
        Width = 800;
        Height = 500;
        var baseUri = new Uri("avares://Everywhere.Core/");
        Resources.MergedDictionaries.Add(new ResourceInclude(baseUri)
        {
            Source = new Uri(baseUri, "Views/Configuration/SettingsItemControl.axaml")
        });
        Resources.MergedDictionaries.Add(new ResourceInclude(baseUri)
        {
            Source = new Uri(baseUri, "Views/Configuration/SettingsItemsControl.axaml")
        });
        content.Theme = (ControlTheme?)Resources[typeof(SettingsItemsControl)];
        Styles.Add(new Style(selector => selector.OfType<SettingsItemControl>())
        {
            Setters = { new Setter(ThemeProperty, Resources[typeof(SettingsItemControl)]) }
        });
        Content = content;
    }
}