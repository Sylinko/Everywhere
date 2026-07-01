// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

#if IsMacOS
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using MonoMod;

namespace Everywhere.Patches.Avalonia.Controls;

[MonoModPatch("Avalonia.Automation.Peers.ControlAutomationPeer")]
internal class patch_ControlAutomationPeer
{
    [MonoModReplace]
    public static AutomationPeer CreatePeerForElement(Control element)
    {
        return EmptyAutomationPeer.Shared;
    }

    private sealed class EmptyAutomationPeer : AutomationPeer
    {
        public static EmptyAutomationPeer Shared { get; } = new();

        protected override void BringIntoViewCore() { }

        protected override string? GetAcceleratorKeyCore() => null;

        protected override string? GetAccessKeyCore() => null;

        protected override AutomationControlType GetAutomationControlTypeCore() => default;

        protected override string? GetAutomationIdCore() => null;

        protected override Rect GetBoundingRectangleCore() => default;

        protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() => [];

        protected override string GetClassNameCore() => string.Empty;

        protected override AutomationPeer? GetLabeledByCore() => null;

        protected override string? GetNameCore() => null;

        protected override AutomationPeer? GetParentCore() => null;

        protected override bool HasKeyboardFocusCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override bool IsControlElementCore() => false;

        protected override bool IsEnabledCore() => false;

        protected override bool IsKeyboardFocusableCore() => false;

        protected override void SetFocusCore() { }

        protected override bool ShowContextMenuCore() => false;

        public override bool TrySetParent(AutomationPeer? parent) => false;
    }
}
#endif