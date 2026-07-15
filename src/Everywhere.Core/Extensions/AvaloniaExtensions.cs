using Avalonia.Controls;
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

    public static TextBlock ToTextBlock(this IDynamicLocaleKey dynamicResourceKey)
    {
        return new TextBlock
        {
            Classes = { nameof(DynamicLocaleKey) },
            [!TextBlock.TextProperty] = dynamicResourceKey.ToBinding()
        };
    }
}