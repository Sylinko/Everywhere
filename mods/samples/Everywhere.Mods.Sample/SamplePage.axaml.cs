using Avalonia.Controls;
using Avalonia.Interactivity;
using Everywhere.I18N;
using Everywhere.Mods.Sample.I18N;
using Everywhere.Views;
using Lucide.Avalonia;

namespace Everywhere.Mods.Sample;

/// <summary>
/// A compiled XAML page whose visible text follows the application's current language.
/// </summary>
public partial class SamplePage : UserControl, IMainViewNavigationTopLevelItem
{
    /// <inheritdoc />
    public int Index => 50;

    /// <inheritdoc />
    public LucideIconKind Icon => LucideIconKind.Box;

    /// <inheritdoc />
    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.SampleMod_Title);

    /// <summary>
    /// Gets the localized explanation of this contribution.
    /// </summary>
    public IDynamicLocaleKey DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.SampleMod_Description);

    /// <summary>
    /// Gets the language-switching instructions.
    /// </summary>
    public IDynamicLocaleKey InstructionsKey { get; } = new DynamicLocaleKey(LocaleKey.SampleMod_Instructions);

    /// <summary>
    /// Gets the action label.
    /// </summary>
    public IDynamicLocaleKey ButtonKey { get; } = new DynamicLocaleKey(LocaleKey.SampleMod_Button);

    /// <summary>
    /// Gets the action result without resolving it before presentation.
    /// </summary>
    public IDynamicLocaleKey ResultKey { get; } = new DynamicLocaleKey(LocaleKey.SampleMod_Result);

    /// <summary>
    /// Builds the mod's embedded view on the UI thread.
    /// </summary>
    public SamplePage()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void HandleButtonClick(object? sender, RoutedEventArgs e)
    {
        ResultText.IsVisible = true;
    }
}