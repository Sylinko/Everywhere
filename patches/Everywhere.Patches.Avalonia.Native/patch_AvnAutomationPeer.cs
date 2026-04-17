// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

#if IsMacOS
using Avalonia.Native.Interop;
using MonoMod;

namespace Everywhere.Patches.Avalonia.Native;

[MonoModPatch("Avalonia.Native.AvnAutomationPeer")]
internal class patch_AvnAutomationPeer
{
    [MonoModReplace]
    public void SetNode(IAvnAutomationNode peer)
    {
        // Skip original method
    }
}
#endif