using Avalonia.Controls;
using Avalonia.Input;
using Everywhere.Common;
using ShadUI;

namespace Everywhere.Extensions;

public static class AvaloniaExtensions
{
    public static AnonymousExceptionHandler ToExceptionHandler(this DialogHost dialogHost) => new((exception, message, source, lineNumber) =>
        dialogHost.CreateDialog(exception.GetFriendlyMessage().ToString() ?? "Unknown error", $"[{source}:{lineNumber}] {message ?? "Error"}"));

    public static AnonymousExceptionHandler ToExceptionHandler(this ToastHost toastHost) => new((exception, message, source, lineNumber) =>
        toastHost.CreateToast($"[{source}:{lineNumber}] {message ?? "Error"}")
            .WithContent(exception.GetFriendlyMessage().ToTextBlock())
            .DismissOnClick()
            .ShowError());

    extension(KeyModifiers modifiers)
    {
        /// <summary>
        /// Whether these modifier keys should trigger a standard application shortcut such as copy, paste,
        /// or find: <see cref="KeyModifiers.Control"/> on every platform, plus <see cref="KeyModifiers.Meta"/>
        /// on macOS where it is the Command key.
        /// </summary>
        /// <remarks>
        /// Avalonia reports the macOS Command key as <see cref="KeyModifiers.Meta"/>, so shortcuts written
        /// against <see cref="KeyModifiers.Control"/> alone never match the native Command shortcut on macOS.
        /// <see cref="KeyModifiers.Meta"/> is deliberately not accepted on other platforms: it is the Windows
        /// key there, and Win+V is reserved by the system clipboard history.
        /// </remarks>
        public bool HasApplicationShortcutModifier()
        {
            return modifiers.HasFlag(KeyModifiers.Control) ||
                OperatingSystem.IsMacOS() && modifiers.HasFlag(KeyModifiers.Meta);
        }

        /// <summary>
        /// Whether the modifiers are exactly the application shortcut modifier plus all requested additional keys.
        /// Additional keys are modifiers such as Shift or Alt, not the application shortcut modifier itself.
        /// See <see cref="HasApplicationShortcutModifier"/>.
        /// </summary>
        public bool IsApplicationShortcutModifierOnly(KeyModifiers additional = KeyModifiers.None)
        {
            return modifiers.HasFlag(additional) && (modifiers & ~additional) switch
            {
                KeyModifiers.Control => true,
                KeyModifiers.Meta when OperatingSystem.IsMacOS() => true,
                _ => false
            };
        }
    }

    public static TextBlock ToTextBlock(this IDynamicLocaleKey dynamicResourceKey)
    {
        return new TextBlock
        {
            Classes = { nameof(DynamicLocaleKey) },
            [!TextBlock.TextProperty] = dynamicResourceKey.ToBinding()
        };
    }
}