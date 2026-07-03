using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Everywhere.Common;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;
using ZLinq;

namespace Everywhere.Extensions;

public static class AvaloniaExtensions
{
    public static AnonymousExceptionHandler ToExceptionHandler(this DialogManager dialogManager) => new((exception, message, source, lineNumber) =>
        dialogManager.CreateDialog(exception.GetFriendlyMessage().ToString() ?? "Unknown error", $"[{source}:{lineNumber}] {message ?? "Error"}"));

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

    /// <summary>
    /// Creates a toast whose visible text stays bound to a dynamic locale key.
    /// </summary>
    /// <remarks>
    /// ShadUI's toast title is a plain string, so localized Prompt Manager toasts place the
    /// title text in the content area as a bound <see cref="TextBlock"/> instead of resolving
    /// the key once before the toast is displayed.
    /// </remarks>
    public static ToastBuilder CreateToast(this ToastHost toastHost, IDynamicLocaleKey titleKey) =>
        toastHost.CreateToast(string.Empty).WithContent(titleKey.ToTextBlock());

    /// <summary>
    /// Creates a toast with localized title and body content.
    /// </summary>
    public static ToastBuilder CreateToast(
        this ToastHost toastHost,
        IDynamicLocaleKey titleKey,
        IDynamicLocaleKey contentKey) =>
        toastHost.CreateToast(string.Empty).WithContent(CreateToastContent(titleKey, contentKey));

    private static StackPanel CreateToastContent(IDynamicLocaleKey titleKey, IDynamicLocaleKey contentKey)
    {
        var title = titleKey.ToTextBlock();
        title.FontWeight = FontWeight.DemiBold;

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                title,
                contentKey.ToTextBlock()
            }
        };
    }

    extension(IServiceCollection services)
    {
        public IServiceCollection AddDialogManagerAndToastManager()
        {
            return services
                .AddTransient<DialogManager>(_ => TryGetReactiveHost()?.DialogHost.Manager ?? new DialogManager())
                .AddTransient<ToastHost>(_ => TryGetReactiveHost()?.ToastHost ?? new ToastHost());

            IReactiveHost? TryGetReactiveHost()
            {
                if (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime is not { } lifetime) return null;

                return lifetime.Windows.AsValueEnumerable().FirstOrDefault(w => w.IsActive) as IReactiveHost ??
                    lifetime.MainWindow as IReactiveHost ??
                    lifetime.Windows.AsValueEnumerable().OfType<IReactiveHost>().FirstOrDefault();
            }
        }
    }
}