using Avalonia.Controls;
using Tabler.Avalonia;

namespace Everywhere.Views.Modding;

/// <summary>
/// Presents a first-visit overview of modding capabilities, sources, and security boundaries.
/// </summary>
public sealed partial class ModdingIntroduction : UserControl
{
    /// <summary>
    /// Gets the introductory feature and trust cards shown in the dialog body.
    /// </summary>
    public IReadOnlyList<ModdingIntroductionCard> Cards { get; } =
    [
        Card(
            TablerIconKind.Puzzle,
            LocaleKey.ModPage_IntroductionDialog_ExtendTitle,
            LocaleKey.ModPage_IntroductionDialog_ExtendBody),
        Card(
            TablerIconKind.Database,
            LocaleKey.ModPage_IntroductionDialog_SourcesTitle,
            LocaleKey.ModPage_IntroductionDialog_SourcesBody),
        Card(
            TablerIconKind.ShieldExclamation,
            LocaleKey.ModPage_IntroductionDialog_SecurityTitle,
            LocaleKey.ModPage_IntroductionDialog_SecurityBody)
    ];

    /// <summary>
    /// Initializes a new <see cref="ModdingIntroduction"/> dialog content view.
    /// </summary>
    public ModdingIntroduction()
    {
        InitializeComponent();
        DataContext = this;
    }

    private static ModdingIntroductionCard Card(TablerIconKind icon, string titleKey, string bodyKey) =>
        new(icon, new DynamicLocaleKey(titleKey), new DynamicLocaleKey(bodyKey));
}

/// <summary>
/// Represents one localized explanation card in the modding welcome dialog.
/// </summary>
public sealed record ModdingIntroductionCard(TablerIconKind Icon, IDynamicLocaleKey TitleKey, IDynamicLocaleKey BodyKey);
