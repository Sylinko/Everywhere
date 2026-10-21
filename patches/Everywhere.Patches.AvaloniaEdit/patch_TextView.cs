using Avalonia;
using MonoMod;

namespace Everywhere.Patches.AvaloniaEdit;

/// <summary>
/// Keeps oversized bring-into-view requests stable in compact editor viewports.
/// </summary>
[MonoModPatch("AvaloniaEdit.Rendering.TextView")]
internal class patch_TextView
{
    [MonoModIgnore]
    private Size _scrollViewport;

    private extern void orig_MakeVisible(Rect rectangle);

    public virtual void MakeVisible(Rect rectangle)
    {
        // SelectionMouseHandler asks to reveal the caret with a five-pixel margin. In a
        // single-line editor this rectangle can be taller than the entire viewport. The
        // original MakeVisible then alternates between centering it and aligning its top.
        // Each ScrollOffsetChanged synchronously extends the mouse selection and requests
        // visibility again, overflowing the stack before the pointer release can finish.
        //
        // An oversized target cannot be fully visible. Request its centered, viewport-sized
        // slice instead. The caret remains at the center of its requested margin rather than
        // being clipped to preserve an impossible margin. Repeated requests are idempotent,
        // including when the original method clamps a negative offset at the document start.
        // Delegate normal-sized requests and all scroll notifications to the original method.
        if (rectangle.Width > _scrollViewport.Width)
        {
            var x = rectangle.X + (rectangle.Width - _scrollViewport.Width) / 2;
            rectangle = new Rect(x, rectangle.Y, _scrollViewport.Width, rectangle.Height);
        }

        if (rectangle.Height > _scrollViewport.Height)
        {
            var y = rectangle.Y + (rectangle.Height - _scrollViewport.Height) / 2;
            rectangle = new Rect(rectangle.X, y, rectangle.Width, _scrollViewport.Height);
        }

        orig_MakeVisible(rectangle);
    }
}