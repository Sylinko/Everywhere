using FlaUI.Core.AutomationElements;
using FlaUI.Core.Identifiers;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;
using FlaUI.UIA3.Patterns;
using Interop.UIAutomationClient;

namespace Everywhere.Windows.Interop;

public static class AutomationExtension
{
    extension(AutomationElement element)
    {
        /// <summary>
        /// UIA3 throws a lot of exceptions and causing other issues, use this instead
        /// </summary>
        /// <param name="pattern"></param>
        /// <param name="transformer"></param>
        /// <typeparam name="TNativePattern"></typeparam>
        /// <typeparam name="TPattern"></typeparam>
        /// <returns></returns>
        private TPattern? TryGetPattern<TNativePattern, TPattern>(
            PatternId pattern,
            Func<UIA3FrameworkAutomationElement, TNativePattern, TPattern> transformer) where TPattern : class
        {
            try
            {
                if (element.FrameworkAutomationElement is not UIA3FrameworkAutomationElement uia3Element) return null;
                var result = uia3Element.NativeElement.GetCurrentPattern(pattern.Id);
                return result is null ? null : transformer(uia3Element, (TNativePattern)result);
            }
            catch
            {
                return null;
            }
        }

        public IValuePattern? TryGetValuePattern() =>
            TryGetPattern<IUIAutomationValuePattern, ValuePattern>(
                element,
                ValuePattern.Pattern,
                (e, p) => new ValuePattern(e, p));

        public ITextPattern? TryGetTextPattern() =>
            TryGetPattern<IUIAutomationTextPattern, TextPattern>(
                element,
                TextPattern.Pattern,
                (e, p) => new TextPattern(e, p));

        public IInvokePattern? TryGetInvokePattern() =>
            TryGetPattern<IUIAutomationInvokePattern, InvokePattern>(
                element,
                InvokePattern.Pattern,
                (e, p) => new InvokePattern(e, p));

        public ITogglePattern? TryGetTogglePattern() =>
            TryGetPattern<IUIAutomationTogglePattern, TogglePattern>(
                element,
                TogglePattern.Pattern,
                (e, p) => new TogglePattern(e, p));

        public IExpandCollapsePattern? TryGetExpandCollapsePattern() =>
            TryGetPattern<IUIAutomationExpandCollapsePattern, ExpandCollapsePattern>(
                element,
                ExpandCollapsePattern.Pattern,
                (e, p) => new ExpandCollapsePattern(e, p));

        public ISelectionItemPattern? TryGetSelectionItemPattern() =>
            TryGetPattern<IUIAutomationSelectionItemPattern, SelectionItemPattern>(
                element,
                SelectionItemPattern.Pattern,
                (e, p) => new SelectionItemPattern(e, p));

        public ILegacyIAccessiblePattern? TryGetLegacyIAccessiblePattern() =>
            TryGetPattern<IUIAutomationLegacyIAccessiblePattern, LegacyIAccessiblePattern>(
                element,
                LegacyIAccessiblePattern.Pattern,
                (e, p) => new LegacyIAccessiblePattern(e, p));
    }
}