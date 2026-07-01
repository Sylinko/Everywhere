using Everywhere.Chat;

namespace Everywhere.Views;

public interface IVisualElementAnimationTarget
{
    bool IsKeyboardFocusWithin { get; }

    bool IsVisible { get; }

    /// <summary>
    /// Tries to get the center point of the specified attachment on the screen coordinates.
    /// This is used for the animation effect to determine where the visual element should fly to.
    /// </summary>
    /// <param name="attachment"></param>
    /// <param name="center"></param>
    /// <returns></returns>
    bool TryGetAttachmentCenterOnScreen(ChatAttachment attachment, out PixelPoint center);
}