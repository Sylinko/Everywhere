using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat.Links;
using LiveMarkdown.Avalonia;
using Lucide.Avalonia;

namespace Everywhere.Views;

public static class ChatLinkMenu
{
    public static ContextMenu Create(TopLevel owner, ChatLinkInteraction interaction)
    {
        var menu = new ContextMenu
        {
            Cursor = new Cursor(StandardCursorType.Arrow),
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 0 })
        };
        menu.Classes.Add("ChatLinkMenu");
        menu.DataContextChanged += (_, _) =>
        {
            var items = menu.DataContext switch
            {
                Link { HRef: { } address } link => CreateItems(owner, interaction, ChatLinkTarget.Parse(address.OriginalString), GetText(link), link),
                ChatLinkTarget target => CreateItems(owner, interaction, target, target.Address, null),
                _ => null
            };
            menu.ItemsSource = items;
        };
        menu.Closed += (_, _) => menu.DataContext = null;
        return menu;
    }

    private static List<object> CreateItems(TopLevel owner, ChatLinkInteraction interaction, ChatLinkTarget target, string label, Link? link)
    {
        var result = new List<object>();
        switch (target.Kind)
        {
            case ChatLinkKind.Local:
                result.Add(MakeItem(LocaleKey.ChatLink_Reveal, ChatLinkAction.Reveal, LucideIconKind.FolderOpen));

                if (ChatLinkInteraction.IsExecutableExtension(target.Address))
                {
                    result.Add(MakeItem(LocaleKey.ChatLink_Run, ChatLinkAction.Open, LucideIconKind.AppWindow));
                }
                else
                {
                    result.Add(MakeItem(LocaleKey.ChatLink_Open, ChatLinkAction.Open, LucideIconKind.ExternalLink));
                }

                result.Add(MakeItem(LocaleKey.ChatLink_OpenWith, ChatLinkAction.OpenWith));
                result.Add(MakeItem(LocaleKey.ChatLink_SaveAs, ChatLinkAction.SaveAs));
                result.Add(new Separator());
                result.Add(MakeItem(LocaleKey.ChatLink_CopyPath, ChatLinkAction.CopyAddress, LucideIconKind.Copy));
                break;
            case ChatLinkKind.Web:
                result.Add(MakeItem(LocaleKey.Common_OpenLink, ChatLinkAction.Open, LucideIconKind.ExternalLink));
                result.Add(new Separator());
                result.Add(MakeItem(LocaleKey.Common_CopyLink, ChatLinkAction.CopyAddress, LucideIconKind.Copy));
                break;
            default:
                result.Add(MakeItem(LocaleKey.Common_CopyLink, ChatLinkAction.CopyAddress, LucideIconKind.Copy));
                break;
        }

        result.Add(MakeItem(LocaleKey.Common_CopyLinkText, ChatLinkAction.CopyText));
        return result;

        MenuItem MakeItem(string key, ChatLinkAction action, LucideIconKind? icon = null)
        {
            var item = new MenuItem();
            if (icon is { } kind) item.Icon = new LucideIcon { Kind = kind, Size = 16 };
            item[!HeaderedSelectingItemsControl.HeaderProperty] = new DynamicLocaleKey(key).ToBinding();
            item.Command = new AsyncRelayCommand(() =>
            {
                // Match LiveMarkdown's left-click visited state. Copying and saving are not navigation.
                if (link is not null && (action is ChatLinkAction.Open or ChatLinkAction.Reveal or ChatLinkAction.OpenWith))
                {
                    link.IsClicked = true;
                }

                return interaction.ExecuteAsync(owner, target, action, label);
            });
            return item;
        }
    }

    private static string GetText(Inline inline) => inline switch
    {
        Run run => run.Text ?? string.Empty,
        Span span => string.Concat(span.Inlines.Select(GetText)),
        LineBreak => Environment.NewLine,
        _ => string.Empty
    };
}