using Everywhere.Interop;

namespace Everywhere.Mac.Interop;

internal static class NSAlertMessageBox
{
    public static NativeMessageBoxResult Show(string title, string message, NativeMessageBoxButtons buttons, NativeMessageBoxIcon icon)
    {
        using var alert = new NSAlert();
        alert.AlertStyle = icon switch
        {
            NativeMessageBoxIcon.Error or NativeMessageBoxIcon.Hand or NativeMessageBoxIcon.Stop => NSAlertStyle.Critical,
            NativeMessageBoxIcon.Warning => NSAlertStyle.Warning,
            _ => NSAlertStyle.Informational
        };
        alert.MessageText = title;
        alert.InformativeText = message;
        switch (buttons)
        {
            case NativeMessageBoxButtons.OkCancel:
            {
                alert.AddButton(CoreLocaleKey.Common_OK.I18N());
                alert.AddButton(CoreLocaleKey.Common_Cancel.I18N());
                break;
            }
            case NativeMessageBoxButtons.YesNo:
            {
                alert.AddButton(CoreLocaleKey.Common_Yes.I18N());
                alert.AddButton(CoreLocaleKey.Common_No.I18N());
                break;
            }
            case NativeMessageBoxButtons.YesNoCancel:
            {
                alert.AddButton(CoreLocaleKey.Common_Yes.I18N());
                alert.AddButton(CoreLocaleKey.Common_No.I18N());
                alert.AddButton(CoreLocaleKey.Common_Cancel.I18N());
                break;
            }
            case NativeMessageBoxButtons.RetryCancel:
            {
                alert.AddButton(CoreLocaleKey.Common_Retry.I18N());
                alert.AddButton(CoreLocaleKey.Common_Cancel.I18N());
                break;
            }
            case NativeMessageBoxButtons.AbortRetryIgnore:
            {
                alert.AddButton(CoreLocaleKey.Common_Abort.I18N());
                alert.AddButton(CoreLocaleKey.Common_Retry.I18N());
                alert.AddButton(CoreLocaleKey.Common_Ignore.I18N());
                break;
            }
            default:
            {
                alert.AddButton(CoreLocaleKey.Common_OK.I18N());
                break;
            }
        }

        var result = (NSAlertButtonReturn)alert.RunModal();
        return result switch
        {
            NSAlertButtonReturn.First => buttons switch
            {
                NativeMessageBoxButtons.Ok => NativeMessageBoxResult.Ok,
                NativeMessageBoxButtons.OkCancel => NativeMessageBoxResult.Ok,
                NativeMessageBoxButtons.YesNo => NativeMessageBoxResult.Yes,
                NativeMessageBoxButtons.YesNoCancel => NativeMessageBoxResult.Yes,
                NativeMessageBoxButtons.RetryCancel => NativeMessageBoxResult.Retry,
                NativeMessageBoxButtons.AbortRetryIgnore => NativeMessageBoxResult.Cancel,
                _ => NativeMessageBoxResult.None
            },
            NSAlertButtonReturn.Second => buttons switch
            {
                NativeMessageBoxButtons.OkCancel => NativeMessageBoxResult.Cancel,
                NativeMessageBoxButtons.YesNo => NativeMessageBoxResult.No,
                NativeMessageBoxButtons.YesNoCancel => NativeMessageBoxResult.No,
                NativeMessageBoxButtons.RetryCancel => NativeMessageBoxResult.Cancel,
                NativeMessageBoxButtons.AbortRetryIgnore => NativeMessageBoxResult.Retry,
                _ => NativeMessageBoxResult.None
            },
            NSAlertButtonReturn.Third => buttons switch
            {
                NativeMessageBoxButtons.YesNoCancel => NativeMessageBoxResult.Cancel,
                NativeMessageBoxButtons.AbortRetryIgnore => NativeMessageBoxResult.Ignore,
                _ => NativeMessageBoxResult.None
            },
            _ => NativeMessageBoxResult.None
        };
    }
}